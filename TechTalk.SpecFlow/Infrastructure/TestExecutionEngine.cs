﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using BoDi;
using TechTalk.SpecFlow.Analytics;
using TechTalk.SpecFlow.Bindings;
using TechTalk.SpecFlow.Bindings.Reflection;
using TechTalk.SpecFlow.Compatibility;
using TechTalk.SpecFlow.Configuration;
using TechTalk.SpecFlow.ErrorHandling;
using TechTalk.SpecFlow.Events;
using TechTalk.SpecFlow.Plugins;
using TechTalk.SpecFlow.Tracing;
using TechTalk.SpecFlow.UnitTestProvider;

namespace TechTalk.SpecFlow.Infrastructure
{
    public class TestExecutionEngine : ITestExecutionEngine
    {
        private readonly IBindingInvoker _bindingInvoker;
        private readonly IBindingRegistry _bindingRegistry;
        private readonly IContextManager _contextManager;
        private readonly IErrorProvider _errorProvider;
        private readonly IObsoleteStepHandler _obsoleteStepHandler;
        private readonly SpecFlowConfiguration _specFlowConfiguration;
        private readonly IStepArgumentTypeConverter _stepArgumentTypeConverter;
        private readonly IStepDefinitionMatchService _stepDefinitionMatchService;
        private readonly IStepFormatter _stepFormatter;
        private readonly ITestObjectResolver _testObjectResolver;
        private readonly ITestTracer _testTracer;
        private readonly IUnitTestRuntimeProvider _unitTestRuntimeProvider;
        private readonly IAnalyticsEventProvider _analyticsEventProvider;
        private readonly IAnalyticsTransmitter _analyticsTransmitter;
        private readonly ITestRunnerManager _testRunnerManager;
        private readonly IRuntimePluginTestExecutionLifecycleEventEmitter _runtimePluginTestExecutionLifecycleEventEmitter;
        private readonly ITestThreadExecutionEventPublisher _testThreadExecutionEventPublisher;
        private CultureInfo _defaultBindingCulture = CultureInfo.CurrentCulture;

        private ProgrammingLanguage _defaultTargetLanguage = ProgrammingLanguage.CSharp;

        private bool _testRunnerEndExecuted = false;
        private object _testRunnerEndExecutedLock = new object();
        private bool _testRunnerStartExecuted = false;
        private ITestPendingMessageFactory _testPendingMessageFactory;
        private ITestUndefinedMessageFactory _testUndefinedMessageFactory;

        public TestExecutionEngine(
            IStepFormatter stepFormatter,
            ITestTracer testTracer,
            IErrorProvider errorProvider,
            IStepArgumentTypeConverter stepArgumentTypeConverter,
            SpecFlowConfiguration specFlowConfiguration,
            IBindingRegistry bindingRegistry,
            IUnitTestRuntimeProvider unitTestRuntimeProvider,
            IContextManager contextManager,
            IStepDefinitionMatchService stepDefinitionMatchService,
            IBindingInvoker bindingInvoker,
            IObsoleteStepHandler obsoleteStepHandler,
            IAnalyticsEventProvider analyticsEventProvider, 
            IAnalyticsTransmitter analyticsTransmitter, 
            ITestRunnerManager testRunnerManager,
            IRuntimePluginTestExecutionLifecycleEventEmitter runtimePluginTestExecutionLifecycleEventEmitter,
            ITestThreadExecutionEventPublisher testThreadExecutionEventPublisher,
            ITestPendingMessageFactory testPendingMessageFactory,
            ITestUndefinedMessageFactory testUndefinedMessageFactory,
            ITestObjectResolver testObjectResolver = null,
            IObjectContainer testThreadContainer = null) //TODO: find a better way to access the container
        {
            _errorProvider = errorProvider;
            _bindingInvoker = bindingInvoker;
            _contextManager = contextManager;
            _unitTestRuntimeProvider = unitTestRuntimeProvider;
            _bindingRegistry = bindingRegistry;
            _specFlowConfiguration = specFlowConfiguration;
            _testTracer = testTracer;
            _stepFormatter = stepFormatter;
            _stepArgumentTypeConverter = stepArgumentTypeConverter;
            _stepDefinitionMatchService = stepDefinitionMatchService;
            _testObjectResolver = testObjectResolver;
            TestThreadContainer = testThreadContainer;
            _obsoleteStepHandler = obsoleteStepHandler;
            _analyticsEventProvider = analyticsEventProvider;
            _analyticsTransmitter = analyticsTransmitter;
            _testRunnerManager = testRunnerManager;
            _runtimePluginTestExecutionLifecycleEventEmitter = runtimePluginTestExecutionLifecycleEventEmitter;
            _testThreadExecutionEventPublisher = testThreadExecutionEventPublisher;
            _testPendingMessageFactory = testPendingMessageFactory;
            _testUndefinedMessageFactory = testUndefinedMessageFactory;
        }

        public FeatureContext FeatureContext => _contextManager.FeatureContext;

        public ScenarioContext ScenarioContext => _contextManager.ScenarioContext;

        public virtual void OnTestRunStart()
        {
            if (_testRunnerStartExecuted)
            {
                return;
            }

            if (_analyticsTransmitter.IsEnabled)
            {
                try
                {
                    var testAssemblyName = _testRunnerManager.TestAssembly.GetName().Name;
                    var projectRunningEvent = _analyticsEventProvider.CreateProjectRunningEvent(testAssemblyName);
                    _analyticsTransmitter.TransmitSpecFlowProjectRunningEvent(projectRunningEvent);
                }
                catch (Exception)
                {
                    // catch all exceptions since we do not want to break anything
                }
            }

            _testRunnerStartExecuted = true;

            _testThreadExecutionEventPublisher.PublishEvent(new TestRunStartedEvent());

            FireEvents(HookType.BeforeTestRun);
        }

        public virtual void OnTestRunEnd()
        {
            lock (_testRunnerEndExecutedLock)
            {
                if (_testRunnerEndExecuted)
                {
                    return;
                }

                _testRunnerEndExecuted = true;
            }

            FireEvents(HookType.AfterTestRun);
            
            _testThreadExecutionEventPublisher.PublishEvent(new TestRunFinishedEvent());
        }

        public virtual void OnFeatureStart(FeatureInfo featureInfo)
        {
            // if the unit test provider would execute the fixture teardown code 
            // only delayed (at the end of the execution), we automatically close 
            // the current feature if necessary
            if (_unitTestRuntimeProvider.DelayedFixtureTearDown && FeatureContext != null)
            {
                OnFeatureEnd();
            }


            _contextManager.InitializeFeatureContext(featureInfo);

            _defaultBindingCulture = FeatureContext.BindingCulture;
            _defaultTargetLanguage = featureInfo.GenerationTargetLanguage;
            
            _testThreadExecutionEventPublisher.PublishEvent(new FeatureStartedEvent(FeatureContext));

            FireEvents(HookType.BeforeFeature);
        }

        public virtual void OnFeatureEnd()
        {
            // if the unit test provider would execute the fixture teardown code 
            // only delayed (at the end of the execution), we ignore the 
            // feature-end call, if the feature has been closed already
            if (_unitTestRuntimeProvider.DelayedFixtureTearDown &&
                FeatureContext == null)
                return;

            FireEvents(HookType.AfterFeature);

            if (_specFlowConfiguration.TraceTimings)
            {
                FeatureContext.Stopwatch.Stop();
                var duration = FeatureContext.Stopwatch.Elapsed;
                _testTracer.TraceDuration(duration, "Feature: " + FeatureContext.FeatureInfo.Title);
            }

            _testThreadExecutionEventPublisher.PublishEvent(new FeatureFinishedEvent(FeatureContext));

            _contextManager.CleanupFeatureContext();
        }

        public virtual void OnScenarioInitialize(ScenarioInfo scenarioInfo)
        {
            _contextManager.InitializeScenarioContext(scenarioInfo);
        }

        public virtual void OnScenarioStart()
        {
            _testThreadExecutionEventPublisher.PublishEvent(new ScenarioStartedEvent(FeatureContext, ScenarioContext));

            try
            {
                FireScenarioEvents(HookType.BeforeScenario);
            }
            catch (Exception ex)
            {
                if (_contextManager.ScenarioContext != null)
                {
                    _contextManager.ScenarioContext.ScenarioExecutionStatus = ScenarioExecutionStatus.TestError;
                    _contextManager.ScenarioContext.TestError = ex;
                }
            }
        }

        public virtual void OnAfterLastStep()
        {
            HandleBlockSwitch(ScenarioBlock.None);

            if (_specFlowConfiguration.TraceTimings)
            {
                _contextManager.ScenarioContext.Stopwatch.Stop();
                var duration = _contextManager.ScenarioContext.Stopwatch.Elapsed;
                _testTracer.TraceDuration(duration, "Scenario: " + _contextManager.ScenarioContext.ScenarioInfo.Title);
            }

            if (_contextManager.ScenarioContext.ScenarioExecutionStatus == ScenarioExecutionStatus.OK)
            {
                return;
            }

            if (_contextManager.ScenarioContext.ScenarioExecutionStatus == ScenarioExecutionStatus.Skipped)
            {
                _unitTestRuntimeProvider.TestIgnore("Scenario ignored using @Ignore tag");
                return;
            }

            if (_contextManager.ScenarioContext.ScenarioExecutionStatus == ScenarioExecutionStatus.StepDefinitionPending)
            {
                string pendingStepExceptionMessage = _testPendingMessageFactory.BuildFromScenarioContext(_contextManager.ScenarioContext);
                _errorProvider.ThrowPendingError(_contextManager.ScenarioContext.ScenarioExecutionStatus, pendingStepExceptionMessage);
                return;
            }

            if (_contextManager.ScenarioContext.ScenarioExecutionStatus == ScenarioExecutionStatus.UndefinedStep)
            {
                string undefinedStepExceptionMessage = _testUndefinedMessageFactory.BuildFromContext(_contextManager.ScenarioContext, _contextManager.FeatureContext);
                _errorProvider.ThrowPendingError(_contextManager.ScenarioContext.ScenarioExecutionStatus, undefinedStepExceptionMessage);
                return;
            }

            if (_contextManager.ScenarioContext.TestError == null)
            {
                throw new InvalidOperationException("test failed with an unknown error");
            }

            _contextManager.ScenarioContext.TestError.PreserveStackTrace();
            throw _contextManager.ScenarioContext.TestError;
        }

        public virtual void OnScenarioEnd()
        {
            try
            {
                if (_contextManager.ScenarioContext.ScenarioExecutionStatus != ScenarioExecutionStatus.Skipped)
                {
                    FireScenarioEvents(HookType.AfterScenario);
                }
                _testThreadExecutionEventPublisher.PublishEvent(new ScenarioFinishedEvent(FeatureContext, ScenarioContext));
            }
            finally
            {
                _contextManager.CleanupScenarioContext();
            }
        }

        public virtual void OnScenarioSkipped()
        {
            // after discussing the placement of message sending points, this placement causes far less effort than rewriting the whole logic
            _contextManager.ScenarioContext.ScenarioExecutionStatus = ScenarioExecutionStatus.Skipped;

            // in case of skipping a Scenario, the OnScenarioStart() is not called, so publish the event here
            _testThreadExecutionEventPublisher.PublishEvent(new ScenarioStartedEvent(FeatureContext, ScenarioContext));
            _testThreadExecutionEventPublisher.PublishEvent(new ScenarioSkippedEvent());
        }

        public virtual void Pending()
        {
            throw _errorProvider.GetPendingStepDefinitionError();
        }

        protected virtual void OnBlockStart(ScenarioBlock block)
        {
            if (block == ScenarioBlock.None)
                return;

            FireScenarioEvents(HookType.BeforeScenarioBlock);
        }

        protected virtual void OnBlockEnd(ScenarioBlock block)
        {
            if (block == ScenarioBlock.None)
                return;

            FireScenarioEvents(HookType.AfterScenarioBlock);
        }

        protected virtual void OnStepStart()
        {
            FireScenarioEvents(HookType.BeforeStep);
        }

        protected virtual void OnStepEnd()
        {
            FireScenarioEvents(HookType.AfterStep);
        }
        protected virtual void OnSkipStep()
        {
            _contextManager.StepContext.Status = ScenarioExecutionStatus.Skipped;
            _testTracer.TraceStepSkipped();
            _testThreadExecutionEventPublisher.PublishEvent(new StepSkippedEvent());


            var skippedStepHandlers = _contextManager.ScenarioContext.ScenarioContainer.ResolveAll<ISkippedStepHandler>().ToArray();
            foreach (var skippedStepHandler in skippedStepHandlers)
            {
                skippedStepHandler.Handle(_contextManager.ScenarioContext);
            }
        }

        #region Step/event execution

        protected virtual void FireScenarioEvents(HookType bindingEvent)
        {
            FireEvents(bindingEvent);
        }

        private void FireEvents(HookType hookType)
        {
            _testThreadExecutionEventPublisher.PublishEvent(new HookStartedEvent(hookType, FeatureContext, ScenarioContext, _contextManager.StepContext));
            var stepContext = _contextManager.GetStepContext();

            var matchingHooks = _bindingRegistry.GetHooks(hookType)
                .Where(hookBinding => !hookBinding.IsScoped ||
                                      hookBinding.BindingScope.Match(stepContext, out int _));

            //HACK: The InvokeHook requires an IHookBinding that contains the scope as well
            // if multiple scopes match the same method, we take the first one. 
            // The InvokeHook uses only the Method anyway...
            // The only problem could be if the same method is decorated with hook attributes using different order, 
            // but in this case it is anyway impossible to tell the right ordering.
            var uniqueMatchingHooks = matchingHooks.GroupBy(hookBinding => hookBinding.Method).Select(g => g.First());
            Exception hookException = null;
            try
            {
                //Note: if a (user-)hook throws an exception the subsequent hooks of the same type are not executed
                foreach (var hookBinding in uniqueMatchingHooks.OrderBy(x => x.HookOrder))
                {
                    InvokeHook(_bindingInvoker, hookBinding, hookType);
                }
            }
            catch (Exception hookExceptionCaught)
            {
                hookException = hookExceptionCaught;
                SetHookError(hookType, hookException);
            }

            //Note: plugin-hooks are still executed even if a user-hook failed with an exception
            //A plugin-hook should not throw an exception under normal circumstances, exceptions are not handled/caught here
            FireRuntimePluginTestExecutionLifecycleEvents(hookType);

            _testThreadExecutionEventPublisher.PublishEvent(new HookFinishedEvent(hookType, FeatureContext, ScenarioContext, _contextManager.StepContext, hookException));

            //Note: the (user-)hook exception (if any) will be thrown after the plugin hooks executed to fail the test with the right error  
            if (hookException != null) throw hookException;
        }

        private void FireRuntimePluginTestExecutionLifecycleEvents(HookType hookType)
        {
            //We pass a container corresponding the type of event
            var container = GetHookContainer(hookType);
            _runtimePluginTestExecutionLifecycleEventEmitter.RaiseExecutionLifecycleEvent(hookType, container);
        }

        protected IObjectContainer TestThreadContainer { get; }

        public virtual void InvokeHook(IBindingInvoker invoker, IHookBinding hookBinding, HookType hookType)
        {
            var currentContainer = GetHookContainer(hookType);
            var arguments = ResolveArguments(hookBinding, currentContainer);

            _testThreadExecutionEventPublisher.PublishEvent(new HookBindingStartedEvent(hookBinding));
            TimeSpan duration = default;

            try
            {

                invoker.InvokeBinding(hookBinding, _contextManager, arguments, _testTracer, out duration);
            }
            finally
            {
                _testThreadExecutionEventPublisher.PublishEvent(new HookBindingFinishedEvent(hookBinding, duration));
            }
        }

        private IObjectContainer GetHookContainer(HookType hookType)
        {
            IObjectContainer currentContainer;
            switch (hookType)
            {
                case HookType.BeforeTestRun:
                case HookType.AfterTestRun:
                    currentContainer = TestThreadContainer;
                    break;
                case HookType.BeforeFeature:
                case HookType.AfterFeature:
                    currentContainer = FeatureContext.FeatureContainer;
                    break;
                default: // scenario scoped hooks
                    currentContainer = ScenarioContext.ScenarioContainer;
                    break;
            }

            return currentContainer;
        }

        private SpecFlowContext GetHookContext(HookType hookType)
        {
            switch (hookType)
            {
                case HookType.BeforeTestRun:
                case HookType.AfterTestRun:
                    return _contextManager.TestThreadContext;
                case HookType.BeforeFeature:
                case HookType.AfterFeature:
                    return _contextManager.FeatureContext;
                default: // scenario scoped hooks
                    return _contextManager.ScenarioContext;
            }
        }

        private void SetHookError(HookType hookType, Exception hookException)
        {
            var context = GetHookContext(hookType);
            if (context != null && context.TestError == null)
                context.TestError = hookException;

            if (context is ScenarioContext scenarioContext)
            {
                scenarioContext.ScenarioExecutionStatus = ScenarioExecutionStatus.TestError;
            }
        }

        private object[] ResolveArguments(IHookBinding hookBinding, IObjectContainer currentContainer)
        {
            if (hookBinding.Method == null || !hookBinding.Method.Parameters.Any())
                return null;
            return hookBinding.Method.Parameters.Select(p => ResolveArgument(currentContainer, p)).ToArray();
        }

        private object ResolveArgument(IObjectContainer container, IBindingParameter parameter)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));

            var runtimeParameterType = parameter.Type as RuntimeBindingType;
            if (runtimeParameterType == null)
                throw new SpecFlowException("Parameters can only be resolved for runtime methods.");

            return _testObjectResolver.ResolveBindingInstance(runtimeParameterType.Type, container);
        }

        private void ExecuteStep(IContextManager contextManager, StepInstance stepInstance)
        {
            HandleBlockSwitch(stepInstance.StepDefinitionType.ToScenarioBlock());

            _testTracer.TraceStep(stepInstance, true);

            bool isStepSkipped = contextManager.ScenarioContext.ScenarioExecutionStatus != ScenarioExecutionStatus.OK;
            bool onStepStartExecuted = false;

            BindingMatch match = null;
            object[] arguments = null;
            TimeSpan duration = TimeSpan.Zero;
            try
            {
                match = GetStepMatch(stepInstance);
                contextManager.StepContext.StepInfo.BindingMatch = match;
                contextManager.StepContext.StepInfo.StepInstance = stepInstance;

                if (isStepSkipped)
                {
                    OnSkipStep();
                }
                else
                {
                    arguments = GetExecuteArguments(match);
                    _obsoleteStepHandler.Handle(match);

                    onStepStartExecuted = true;
                    OnStepStart();
                    ExecuteStepMatch(match, arguments, out duration);
                    if (_specFlowConfiguration.TraceSuccessfulSteps)
                        _testTracer.TraceStepDone(match, arguments, duration);
                }
            }
            catch (PendingStepException)
            {
                Debug.Assert(match != null);
                Debug.Assert(arguments != null);

                _testTracer.TraceStepPending(match, arguments);
                contextManager.ScenarioContext.PendingSteps.Add(
                    _stepFormatter.GetMatchText(match, arguments));

                UpdateStatusOnStepFailure(ScenarioExecutionStatus.StepDefinitionPending, null);
            }
            catch (MissingStepDefinitionException)
            {
                UpdateStatusOnStepFailure(ScenarioExecutionStatus.UndefinedStep, null);
            }
            catch (BindingException ex)
            {
                _testTracer.TraceBindingError(ex);
                UpdateStatusOnStepFailure(ScenarioExecutionStatus.BindingError, ex);

            }
            catch (Exception ex)
            {
                _testTracer.TraceError(ex, duration);

                UpdateStatusOnStepFailure(ScenarioExecutionStatus.TestError, ex);

                if (_specFlowConfiguration.StopAtFirstError)
                    throw;
            }
            finally
            {
                if (onStepStartExecuted)
                {
                    OnStepEnd();
                }
            }
        }

        private void UpdateStatusOnStepFailure(ScenarioExecutionStatus stepStatus, Exception exception)
        {
            _contextManager.StepContext.Status = stepStatus;

            if (_contextManager.ScenarioContext.ScenarioExecutionStatus < stepStatus)
            {
                _contextManager.ScenarioContext.ScenarioExecutionStatus = stepStatus;

                if (exception != null)
                {
                    _contextManager.ScenarioContext.TestError = exception;
                }
            }
        }

        protected virtual BindingMatch GetStepMatch(StepInstance stepInstance)
        {
            var match = _stepDefinitionMatchService.GetBestMatch(stepInstance, FeatureContext.BindingCulture, out var ambiguityReason, out var candidatingMatches);

            if (match.Success)
                return match;

            if (candidatingMatches.Any())
            {
                if (ambiguityReason == StepDefinitionAmbiguityReason.AmbiguousSteps)
                    throw _errorProvider.GetAmbiguousMatchError(candidatingMatches, stepInstance);

                if (ambiguityReason == StepDefinitionAmbiguityReason.ParameterErrors) // ambiguouity, because of param error
                    throw _errorProvider.GetAmbiguousBecauseParamCheckMatchError(candidatingMatches, stepInstance);
            }

            _testTracer.TraceNoMatchingStepDefinition(stepInstance, FeatureContext.FeatureInfo.GenerationTargetLanguage, FeatureContext.BindingCulture, candidatingMatches);
            _contextManager.ScenarioContext.MissingSteps.Add(stepInstance);
            throw _errorProvider.GetMissingStepDefinitionError();
        }

        protected virtual void ExecuteStepMatch(BindingMatch match, object[] arguments, out TimeSpan duration)
        {
            _testThreadExecutionEventPublisher.PublishEvent(new StepBindingStartedEvent(match.StepBinding));
            duration = default;

            try
            {
                _bindingInvoker.InvokeBinding(match.StepBinding, _contextManager, arguments, _testTracer, out duration);
            }
            finally
            {
                _testThreadExecutionEventPublisher.PublishEvent(new StepBindingFinishedEvent(match.StepBinding, duration));
            }
        }

        private void HandleBlockSwitch(ScenarioBlock block)
        {
            if (_contextManager == null)
            {
                throw new ArgumentNullException(nameof(_contextManager));
            }
            
            if (_contextManager.ScenarioContext == null)
            {
                throw new ArgumentNullException(nameof(_contextManager.ScenarioContext));
            }

            if (_contextManager.ScenarioContext.CurrentScenarioBlock != block)
            {
                if (_contextManager.ScenarioContext.ScenarioExecutionStatus == ScenarioExecutionStatus.OK)
                    OnBlockEnd(_contextManager.ScenarioContext.CurrentScenarioBlock);

                _contextManager.ScenarioContext.CurrentScenarioBlock = block;

                if (_contextManager.ScenarioContext.ScenarioExecutionStatus == ScenarioExecutionStatus.OK)
                    OnBlockStart(_contextManager.ScenarioContext.CurrentScenarioBlock);
            }
        }

        private object[] GetExecuteArguments(BindingMatch match)
        {
            var bindingParameters = match.StepBinding.Method.Parameters.ToArray();
            if (match.Arguments.Length != bindingParameters.Length)
                throw _errorProvider.GetParameterCountError(match, match.Arguments.Length);

            var arguments = match.Arguments.Select(
                    (arg, argIndex) => ConvertArg(arg, bindingParameters[argIndex].Type))
                .ToArray();

            return arguments;
        }

        private object ConvertArg(object value, IBindingType typeToConvertTo)
        {
            Debug.Assert(value != null);
            Debug.Assert(typeToConvertTo != null);

            return _stepArgumentTypeConverter.Convert(value, typeToConvertTo, FeatureContext.BindingCulture);
        }

        #endregion

        #region Given-When-Then

        public virtual void Step(StepDefinitionKeyword stepDefinitionKeyword, string keyword, string text, string multilineTextArg, Table tableArg)
        {
            StepDefinitionType stepDefinitionType = stepDefinitionKeyword == StepDefinitionKeyword.And || stepDefinitionKeyword == StepDefinitionKeyword.But
                ? GetCurrentBindingType()
                : (StepDefinitionType) stepDefinitionKeyword;
            _contextManager.InitializeStepContext(new StepInfo(stepDefinitionType, text, tableArg, multilineTextArg));
            _testThreadExecutionEventPublisher.PublishEvent(new StepStartedEvent(FeatureContext, ScenarioContext, _contextManager.StepContext));

            try
            {
                var stepInstance = new StepInstance(stepDefinitionType, stepDefinitionKeyword, keyword, text, multilineTextArg, tableArg, _contextManager.GetStepContext());
                ExecuteStep(_contextManager, stepInstance);
            }
            finally
            {
                _testThreadExecutionEventPublisher.PublishEvent(new StepFinishedEvent(FeatureContext, ScenarioContext, _contextManager.StepContext));
                _contextManager.CleanupStepContext();
            }
        }

        private StepDefinitionType GetCurrentBindingType()
        {
            return _contextManager.CurrentTopLevelStepDefinitionType ?? StepDefinitionType.Given;
        }

        #endregion
    }
}
