// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Agent.Sdk.Knob;
using Agent.Sdk.Util;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(JobRunner))]
    public interface IJobRunner : IAgentService
    {
        Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken);
        void UpdateMetadata(JobMetadataMessage message);
    }

    public sealed class JobRunner : AgentService, IJobRunner
    {
        private IJobServerQueue _jobServerQueue;
        private ITempDirectoryManager _tempDirectoryManager;
        /// <summary>
        /// Add public accessor for _jobServerQueue to make JobRunner more testable
        /// See /Test/L0/Worker/JobRunnerL0.cs
        /// </summary>
        public IJobServerQueue JobServerQueue
        {
            set => _jobServerQueue = value;
        }
        public async Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Resources, nameof(message.Resources));
            ArgUtil.NotNull(message.Variables, nameof(message.Variables));
            ArgUtil.NotNull(message.Steps, nameof(message.Steps));
            Trace.Info("Job ID {0}", message.JobId);

            DateTime jobStartTimeUtc = DateTime.UtcNow;

            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));

            // System.AccessToken
            if (message.Variables.ContainsKey(Constants.Variables.System.EnableAccessToken) &&
                StringUtil.ConvertToBoolean(message.Variables[Constants.Variables.System.EnableAccessToken].Value))
            {
                message.Variables[Constants.Variables.System.AccessToken] = new VariableValue(systemConnection.Authorization.Parameters["AccessToken"], false);
            }

            // back compat TfsServerUrl
            message.Variables[Constants.Variables.System.TFServerUrl] = systemConnection.Url.AbsoluteUri;

            // Make sure SystemConnection Url and Endpoint Url match Config Url base for OnPremises server
            // System.ServerType will always be there after M133
            if (!message.Variables.ContainsKey(Constants.Variables.System.ServerType) ||
                string.Equals(message.Variables[Constants.Variables.System.ServerType]?.Value, "OnPremises", StringComparison.OrdinalIgnoreCase))
            {
                ReplaceConfigUriBaseInJobRequestMessage(message);
            }

            // Setup the job server and job server queue.
            var jobServer = HostContext.GetService<IJobServer>();
            VssCredentials jobServerCredential = VssUtil.GetVssCredential(systemConnection);
            Uri jobServerUrl = systemConnection.Url;

            Trace.Info($"Creating job server with URL: {jobServerUrl}");
            // jobServerQueue is the throttling reporter.
            _jobServerQueue = HostContext.GetService<IJobServerQueue>();
            VssConnection jobConnection = VssUtil.CreateConnection(
                jobServerUrl,
                jobServerCredential,
                trace: Trace,
                new DelegatingHandler[] { new ThrottlingReportHandler(_jobServerQueue) }
                );
            await jobServer.ConnectAsync(jobConnection);

            _jobServerQueue.Start(message);
            HostContext.WritePerfCounter($"WorkerJobServerQueueStarted_{message.RequestId.ToString()}");

            IExecutionContext jobContext = null;
            CancellationTokenRegistration? agentShutdownRegistration = null;
            VssConnection taskConnection = null;
            VssConnection legacyTaskConnection = null;

            try
            {
                // Create the job execution context.
                jobContext = HostContext.CreateService<IExecutionContext>();
                jobContext.InitializeJob(message, jobRequestCancellationToken);

                Trace.Info("Starting the job execution context.");
                jobContext.Start();
                jobContext.Section(StringUtil.Loc("StepStarting", message.JobDisplayName));

                agentShutdownRegistration = HostContext.AgentShutdownToken.Register(() =>
                {
                    // log an issue, then agent get shutdown by Ctrl-C or Ctrl-Break.
                    // the server will use Ctrl-Break to tells the agent that operating system is shutting down.
                    string errorMessage;
                    switch (HostContext.AgentShutdownReason)
                    {
                        case ShutdownReason.UserCancelled:
                            errorMessage = StringUtil.Loc("UserShutdownAgent");
                            break;
                        case ShutdownReason.OperatingSystemShutdown:
                            errorMessage = StringUtil.Loc("OperatingSystemShutdown", Environment.MachineName);
                            break;
                        default:
                            throw new ArgumentException(HostContext.AgentShutdownReason.ToString(), nameof(HostContext.AgentShutdownReason));
                    }
                    jobContext.AddIssue(new Issue() { Type = IssueType.Error, Message = errorMessage });
                });

                // Validate directory permissions.
                string workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                Trace.Info($"Validating directory permissions for: '{workDirectory}'");
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    IOUtil.ValidateExecutePermission(workDirectory);
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    jobContext.Error(ex);
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                // Set agent variables.
                AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
                jobContext.SetVariable(Constants.Variables.Agent.Id, settings.AgentId.ToString(CultureInfo.InvariantCulture));
                jobContext.SetVariable(Constants.Variables.Agent.HomeDirectory, HostContext.GetDirectory(WellKnownDirectory.Root), isFilePath: true);
                jobContext.SetVariable(Constants.Variables.Agent.JobName, message.JobDisplayName);
                jobContext.SetVariable(Constants.Variables.Agent.CloudId, settings.AgentCloudId);
                jobContext.SetVariable(Constants.Variables.Agent.IsSelfHosted, settings.IsMSHosted ? "0" : "1");
                jobContext.SetVariable(Constants.Variables.Agent.MachineName, Environment.MachineName);
                jobContext.SetVariable(Constants.Variables.Agent.Name, settings.AgentName);
                jobContext.SetVariable(Constants.Variables.Agent.OS, VarUtil.OS);
                jobContext.SetVariable(Constants.Variables.Agent.OSArchitecture, VarUtil.OSArchitecture);
                jobContext.SetVariable(Constants.Variables.Agent.RootDirectory, HostContext.GetDirectory(WellKnownDirectory.Work), isFilePath: true);
                if (PlatformUtil.RunningOnWindows)
                {
                    jobContext.SetVariable(Constants.Variables.Agent.ServerOMDirectory, HostContext.GetDirectory(WellKnownDirectory.ServerOM), isFilePath: true);
                }
                if (!PlatformUtil.RunningOnWindows)
                {
                    jobContext.SetVariable(Constants.Variables.Agent.AcceptTeeEula, settings.AcceptTeeEula.ToString());
                }
                jobContext.SetVariable(Constants.Variables.Agent.WorkFolder, HostContext.GetDirectory(WellKnownDirectory.Work), isFilePath: true);
                jobContext.SetVariable(Constants.Variables.System.WorkFolder, HostContext.GetDirectory(WellKnownDirectory.Work), isFilePath: true);

                try
                {
                    jobContext.SetVariable(Constants.Variables.System.IsAzureVM, PlatformUtil.DetectAzureVM() ? "1" : "0");
                    jobContext.SetVariable(Constants.Variables.System.IsDockerContainer, PlatformUtil.DetectDockerContainer() ? "1" : "0");
                }
                catch (Exception ex)
                {
                    // Error with telemetry shouldn't affect job run
                    Trace.Info($"Couldn't retrieve telemetry information");
                    Trace.Info(ex);
                }

                string toolsDirectory = HostContext.GetDirectory(WellKnownDirectory.Tools);
                Directory.CreateDirectory(toolsDirectory);
                jobContext.SetVariable(Constants.Variables.Agent.ToolsDirectory, toolsDirectory, isFilePath: true);

                if (AgentKnobs.DisableGitPrompt.GetValue(jobContext).AsBoolean())
                {
                    jobContext.SetVariable("GIT_TERMINAL_PROMPT", "0");
                }

                // Setup TEMP directories
                _tempDirectoryManager = HostContext.GetService<ITempDirectoryManager>();
                _tempDirectoryManager.InitializeTempDirectory(jobContext);

                // todo: task server can throw. try/catch and fail job gracefully.
                // prefer task definitions url, then TFS collection url, then TFS account url
                var taskServer = HostContext.GetService<ITaskServer>();
                Uri taskServerUri = null;
                if (!string.IsNullOrEmpty(jobContext.Variables.System_TaskDefinitionsUri))
                {
                    taskServerUri = new Uri(jobContext.Variables.System_TaskDefinitionsUri);
                }
                else if (!string.IsNullOrEmpty(jobContext.Variables.System_TFCollectionUrl))
                {
                    taskServerUri = new Uri(jobContext.Variables.System_TFCollectionUrl);
                }

                var taskServerCredential = VssUtil.GetVssCredential(systemConnection);
                if (taskServerUri != null)
                {
                    Trace.Info($"Creating task server with {taskServerUri}");

                    taskConnection = VssUtil.CreateConnection(taskServerUri, taskServerCredential, Trace);
                    await taskServer.ConnectAsync(taskConnection);
                }

                // for back compat TFS 2015 RTM/QU1, we may need to switch the task server url to agent config url
                if (!string.Equals(message?.Variables.GetValueOrDefault(Constants.Variables.System.ServerType)?.Value, "Hosted", StringComparison.OrdinalIgnoreCase))
                {
                    if (taskServerUri == null || !await taskServer.TaskDefinitionEndpointExist())
                    {
                        Trace.Info($"Can't determine task download url from JobMessage or the endpoint doesn't exist.");
                        var configStore = HostContext.GetService<IConfigurationStore>();
                        taskServerUri = new Uri(configStore.GetSettings().ServerUrl);

                        Trace.Info($"Recreate task server with configuration server url: {taskServerUri}");
                        legacyTaskConnection = VssUtil.CreateConnection(taskServerUri, taskServerCredential, trace: Trace);
                        await taskServer.ConnectAsync(legacyTaskConnection);
                    }
                }

                // Expand the endpoint data values.
                foreach (ServiceEndpoint endpoint in jobContext.Endpoints)
                {
                    jobContext.Variables.ExpandValues(target: endpoint.Data);
                    VarUtil.ExpandEnvironmentVariables(HostContext, target: endpoint.Data);
                }

                // Expand the repository property values.
                foreach (var repository in jobContext.Repositories)
                {
                    // expand checkout option
                    var checkoutOptions = repository.Properties.Get<JToken>(Pipelines.RepositoryPropertyNames.CheckoutOptions);
                    if (checkoutOptions != null)
                    {
                        checkoutOptions = jobContext.Variables.ExpandValues(target: checkoutOptions);
                        checkoutOptions = VarUtil.ExpandEnvironmentVariables(HostContext, target: checkoutOptions);
                        repository.Properties.Set<JToken>(Pipelines.RepositoryPropertyNames.CheckoutOptions, checkoutOptions);
                    }

                    // expand workspace mapping
                    var mappings = repository.Properties.Get<JToken>(Pipelines.RepositoryPropertyNames.Mappings);
                    if (mappings != null)
                    {
                        mappings = jobContext.Variables.ExpandValues(target: mappings);
                        mappings = VarUtil.ExpandEnvironmentVariables(HostContext, target: mappings);
                        repository.Properties.Set<JToken>(Pipelines.RepositoryPropertyNames.Mappings, mappings);
                    }
                }

                // Expand container properties
                foreach (var container in jobContext.Containers)
                {
                    this.ExpandProperties(container, jobContext.Variables);
                }
                foreach (var sidecar in jobContext.SidecarContainers)
                {
                    this.ExpandProperties(sidecar, jobContext.Variables);
                }

                // Get the job extension.
                Trace.Info("Getting job extension.");
                var hostType = jobContext.Variables.System_HostType;
                var extensionManager = HostContext.GetService<IExtensionManager>();
                // We should always have one job extension
                IJobExtension jobExtension =
                    (extensionManager.GetExtensions<IJobExtension>() ?? new List<IJobExtension>())
                    .Where(x => x.HostType.HasFlag(hostType))
                    .FirstOrDefault();
                ArgUtil.NotNull(jobExtension, nameof(jobExtension));

                List<IStep> jobSteps = null;
                try
                {
                    Trace.Info("Initialize job. Getting all job steps.");
                    jobSteps = await jobExtension.InitializeJob(jobContext, message);
                }
                catch (OperationCanceledException ex) when (jobContext.CancellationToken.IsCancellationRequested)
                {
                    // set the job to canceled
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job is canceled during initialize.");
                    Trace.Error($"Caught exception: {ex}");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // set the job to failed.
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job initialize failed.");
                    Trace.Error($"Caught exception from {nameof(jobExtension.InitializeJob)}: {ex}");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                // trace out all steps
                Trace.Info($"Total job steps: {jobSteps.Count}.");
                Trace.Verbose($"Job steps: '{string.Join(", ", jobSteps.Select(x => x.DisplayName))}'");
                HostContext.WritePerfCounter($"WorkerJobInitialized_{message?.RequestId.ToString()}");

                // Run all job steps
                Trace.Info("Run all job steps.");
                var stepsRunner = HostContext.GetService<IStepsRunner>();
                try
                {
                    await stepsRunner.RunAsync(jobContext, jobSteps);
                }
                catch (Exception ex)
                {
                    // StepRunner should never throw exception out.
                    // End up here mean there is a bug in StepRunner
                    // Log the error and fail the job.
                    Trace.Error($"Caught exception from job steps {nameof(StepsRunner)}: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }
                finally
                {
                    Trace.Info("Finalize job.");
                    await jobExtension.FinalizeJob(jobContext);
                }

                Trace.Info($"Job result after all job steps finish: {jobContext.Result ?? TaskResult.Succeeded}");

                if (jobContext.Variables.GetBoolean(Constants.Variables.Agent.Diagnostic) ?? false)
                {
                    Trace.Info("Support log upload starting.");

                    IDiagnosticLogManager diagnosticLogManager = HostContext.GetService<IDiagnosticLogManager>();

                    try
                    {
                        await diagnosticLogManager.UploadDiagnosticLogsAsync(executionContext: jobContext, message: message, jobStartTimeUtc: jobStartTimeUtc);

                        Trace.Info("Support log upload complete.");
                    }
                    catch (Exception ex)
                    {
                        // Log the error but make sure we continue gracefully.
                        Trace.Info("Error uploading support logs.");
                        Trace.Error(ex);
                    }
                }

                Trace.Info("Completing the job execution context.");
                return await CompleteJobAsync(jobServer, jobContext, message);
            }
            catch (AggregateException e)
            {
                ExceptionsUtil.HandleAggregateException((AggregateException)e, Trace.Error);

                return TaskResult.Failed;
            }
            finally
            {
                if (agentShutdownRegistration != null)
                {
                    agentShutdownRegistration.Value.Dispose();
                    agentShutdownRegistration = null;
                }

                legacyTaskConnection?.Dispose();
                taskConnection?.Dispose();
                jobConnection?.Dispose();

                await ShutdownQueue(throwOnFailure: false);
            }
        }

        public void UpdateMetadata(JobMetadataMessage message)
        {
            if (message.PostLinesFrequencyMillis.HasValue && _jobServerQueue != null)
            {
                _jobServerQueue.UpdateWebConsoleLineRate(message.PostLinesFrequencyMillis.Value);
            }
        }

        public void ExpandProperties(ContainerInfo container, Variables variables)
        {
            if (container == null || variables == null)
            {
                return;
            }
            // Expand port mapping
            variables.ExpandValues(container.UserPortMappings);

            // Expand volume mounts
            variables.ExpandValues(container.UserMountVolumes);
            foreach (var volume in container.UserMountVolumes.Values)
            {
                // After mount volume variables are expanded, they are final
                container.MountVolumes.Add(new MountVolume(volume));
            }

            // Expand env vars
            variables.ExpandValues(container.ContainerEnvironmentVariables);

            // Expand image and options strings
            container.ContainerImage = variables.ExpandValue(nameof(container.ContainerImage), container.ContainerImage);
            container.ContainerCreateOptions = variables.ExpandValue(nameof(container.ContainerCreateOptions), container.ContainerCreateOptions);
        }

        private async Task<TaskResult> CompleteJobAsync(IJobServer jobServer, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            ArgUtil.NotNull(message, nameof(message));
            jobContext.Section(StringUtil.Loc("StepFinishing", message.JobDisplayName));
            TaskResult result = jobContext.Complete(taskResult);

            try
            {
                await ShutdownQueue(throwOnFailure: true);
            }
            catch (AggregateException ex)
            {
                ExceptionsUtil.HandleAggregateException((AggregateException)ex, Trace.Error);

                result = TaskResultUtil.MergeTaskResults(result, TaskResult.Failed);
            }
            catch (Exception ex)
            {
                Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                Trace.Error("This indicate a failure during publish output variables. Fail the job to prevent unexpected job outputs.");
                Trace.Error(ex);

                result = TaskResultUtil.MergeTaskResults(result, TaskResult.Failed);
            }

            // Clean TEMP after finish process jobserverqueue, since there might be a pending fileupload still use the TEMP dir.
            _tempDirectoryManager?.CleanupTempDirectory();

            if (!jobContext.Features.HasFlag(PlanFeatures.JobCompletedPlanEvent))
            {
                Trace.Info($"Skip raise job completed event call from worker because Plan version is {message.Plan.Version}");
                return result;
            }

            Trace.Info("Raising job completed event.");
            var jobCompletedEvent = new JobCompletedEvent(message.RequestId, message.JobId, result,
                jobContext.Variables.Get(Constants.Variables.Agent.RunMode) == Constants.Agent.CommandLine.Flags.Once);

            var completeJobRetryLimit = 5;
            var exceptions = new List<Exception>();
            while (completeJobRetryLimit-- > 0)
            {
                try
                {
                    await jobServer.RaisePlanEventAsync(message.Plan.ScopeIdentifier, message.Plan.PlanType, message.Plan.PlanId, jobCompletedEvent, default(CancellationToken));
                    return result;
                }
                catch (TaskOrchestrationPlanNotFoundException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanNotFoundException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (TaskOrchestrationPlanSecurityException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanSecurityException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (Exception ex)
                {
                    Trace.Error($"Catch exception while attempting to raise JobCompletedEvent for job {message.JobId}, job request {message.RequestId}.");
                    Trace.Error(ex);
                    exceptions.Add(ex);
                }

                // delay 5 seconds before next retry.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            // rethrow exceptions from all attempts.
            throw new AggregateException(exceptions);
        }

        private async Task ShutdownQueue(bool throwOnFailure)
        {
            if (_jobServerQueue != null)
            {
                try
                {
                    Trace.Info("Shutting down the job server queue.");
                    await _jobServerQueue.ShutdownAsync();
                }
                catch (AggregateException ex)
                {
                    ExceptionsUtil.HandleAggregateException((AggregateException)ex, Trace.Error);
                }
                catch (Exception ex) when (!throwOnFailure)
                {
                    Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                    Trace.Error(ex);
                }
                finally
                {
                    _jobServerQueue = null; // Prevent multiple attempts.
                }
            }
        }

        // the scheme://hostname:port (how the agent knows the server) is external to our server
        // in other words, an agent may have it's own way (DNS, hostname) of refering
        // to the server.  it owns that.  That's the scheme://hostname:port we will use.
        // Example: Server's notification url is http://tfsserver:8080/tfs
        //          Agent config url is https://tfsserver.mycompany.com:9090/tfs
        private Uri ReplaceWithConfigUriBase(Uri messageUri)
        {
            AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
            try
            {
                Uri result = null;
                Uri configUri = new Uri(settings.ServerUrl);
                if (Uri.TryCreate(new Uri(configUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped)), messageUri.PathAndQuery, out result))
                {
                    //replace the schema and host portion of messageUri with the host from the
                    //server URI (which was set at config time)
                    return result;
                }
            }
            catch (InvalidOperationException ex)
            {
                //cannot parse the Uri - not a fatal error
                Trace.Error(ex);
            }
            catch (UriFormatException ex)
            {
                //cannot parse the Uri - not a fatal error
                Trace.Error(ex);
            }

            return messageUri;
        }

        private void ReplaceConfigUriBaseInJobRequestMessage(Pipelines.AgentJobRequestMessage message)
        {
            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            Uri systemConnectionUrl = systemConnection.Url;

            // fixup any endpoint Url that match SystemConnection Url.
            foreach (var endpoint in message.Resources.Endpoints)
            {
                if (Uri.Compare(endpoint.Url, systemConnectionUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    endpoint.Url = ReplaceWithConfigUriBase(endpoint.Url);
                    Trace.Info($"Ensure endpoint url match config url base. {endpoint.Url}");
                }
            }

            // fixup any repository Url that match SystemConnection Url.
            foreach (var repo in message.Resources.Repositories)
            {
                if (Uri.Compare(repo.Url, systemConnectionUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    repo.Url = ReplaceWithConfigUriBase(repo.Url);
                    Trace.Info($"Ensure repository url match config url base. {repo.Url}");
                }
            }

            // fixup well known variables. (taskDefinitionsUrl, tfsServerUrl, tfsCollectionUrl)
            if (message.Variables.ContainsKey(WellKnownDistributedTaskVariables.TaskDefinitionsUrl))
            {
                string taskDefinitionsUrl = message.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl].Value;
                message.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl] = ReplaceWithConfigUriBase(new Uri(taskDefinitionsUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TaskDefinitionsUrl match config url base. {message.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl].Value}");
            }

            if (message.Variables.ContainsKey(WellKnownDistributedTaskVariables.TFCollectionUrl))
            {
                string tfsCollectionUrl = message.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl].Value;
                message.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl] = ReplaceWithConfigUriBase(new Uri(tfsCollectionUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TFCollectionUrl match config url base. {message.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl].Value}");
            }

            if (message.Variables.ContainsKey(Constants.Variables.System.TFServerUrl))
            {
                string tfsServerUrl = message.Variables[Constants.Variables.System.TFServerUrl].Value;
                message.Variables[Constants.Variables.System.TFServerUrl] = ReplaceWithConfigUriBase(new Uri(tfsServerUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TFServerUrl match config url base. {message.Variables[Constants.Variables.System.TFServerUrl].Value}");
            }
        }
    }
}
