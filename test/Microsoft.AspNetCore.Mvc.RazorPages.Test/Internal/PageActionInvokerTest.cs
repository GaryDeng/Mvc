// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ViewFeatures.Internal;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.Mvc.RazorPages.Internal
{
    public class PageActionInvokerTest : CommonResourceInvokerTest
    {
        #region Diagnostics

        [Fact]
        public async Task Invoke_Success_LogsCorrectValues()
        {
            // Arrange
            var sink = new TestSink();
            var loggerFactory = new TestLoggerFactory(sink, enabled: true);
            var logger = loggerFactory.CreateLogger<PageActionInvoker>();

            var actionDescriptor = CreateDescriptorForSimplePage();
            var displayName = actionDescriptor.DisplayName;

            var invoker = CreateInvoker(filters: null, actionDescriptor: actionDescriptor, logger: logger);

            // Act
            await invoker.InvokeAsync();

            // Assert
            Assert.Single(sink.Scopes);
            Assert.Equal(displayName, sink.Scopes[0].Scope?.ToString());

            Assert.Equal(4, sink.Writes.Count);
            Assert.Equal($"Executing action {displayName}", sink.Writes[0].State?.ToString());
            Assert.Equal($"Executing action method {displayName} with arguments ((null)) - ModelState is Valid", sink.Writes[1].State?.ToString());
            Assert.Equal($"Executed action method {displayName}, returned result {Result.GetType().FullName}.", sink.Writes[2].State?.ToString());
            // This message has the execution time embedded, which we don't want to verify.
            Assert.StartsWith($"Executed action {displayName} ", sink.Writes[3].State?.ToString());
        }

        [Fact]
        public async Task Invoke_WritesDiagnostic_ActionSelected()
        {
            // Arrange
            var actionDescriptor = CreateDescriptorForSimplePage();
            var displayName = actionDescriptor.DisplayName;

            var routeData = new RouteData();
            routeData.Values.Add("tag", "value");

            var listener = new TestDiagnosticListener();

            var invoker = CreateInvoker(filters: null, actionDescriptor: actionDescriptor, listener: listener, routeData: routeData);

            // Act
            await invoker.InvokeAsync();

            // Assert
            Assert.NotNull(listener.BeforeAction?.ActionDescriptor);
            Assert.NotNull(listener.BeforeAction?.HttpContext);

            var routeValues = listener.BeforeAction?.RouteData?.Values;
            Assert.NotNull(routeValues);

            Assert.Equal(1, routeValues.Count);
            Assert.Contains(routeValues, kvp => kvp.Key == "tag" && string.Equals(kvp.Value, "value"));
        }

        [Fact]
        public async Task Invoke_WritesDiagnostic_ActionInvoked()
        {
            // Arrange
            var actionDescriptor = CreateDescriptorForSimplePage();
            var displayName = actionDescriptor.DisplayName;

            var routeData = new RouteData();
            routeData.Values.Add("tag", "value");

            var listener = new TestDiagnosticListener();

            var invoker = CreateInvoker(filters: null, actionDescriptor: actionDescriptor, listener: listener, routeData: routeData);

            // Act
            await invoker.InvokeAsync();

            // Assert
            Assert.NotNull(listener.AfterAction?.ActionDescriptor);
            Assert.NotNull(listener.AfterAction?.HttpContext);
        }

        #endregion

        #region Page Context

        [Fact]
        public async Task AddingValueProviderFactory_AtResourceFilter_IsAvailableInPageContext()
        {
            // Arrange
            var valueProviderFactory2 = Mock.Of<IValueProviderFactory>();
            var resourceFilter = new Mock<IResourceFilter>();
            resourceFilter
                .Setup(f => f.OnResourceExecuting(It.IsAny<ResourceExecutingContext>()))
                .Callback<ResourceExecutingContext>((resourceExecutingContext) =>
                {
                    resourceExecutingContext.ValueProviderFactories.Add(valueProviderFactory2);
                });
            var valueProviderFactory1 = Mock.Of<IValueProviderFactory>();
            var valueProviderFactories = new List<IValueProviderFactory>();
            valueProviderFactories.Add(valueProviderFactory1);

            var invoker = CreateInvoker(
                new IFilterMetadata[] { resourceFilter.Object }, valueProviderFactories: valueProviderFactories);

            // Act
            await invoker.InvokeAsync();

            // Assert
            var controllerContext = Assert.IsType<PageActionInvoker>(invoker).PageContext;
            Assert.NotNull(controllerContext);
            Assert.Equal(2, controllerContext.ValueProviderFactories.Count);
            Assert.Same(valueProviderFactory1, controllerContext.ValueProviderFactories[0]);
            Assert.Same(valueProviderFactory2, controllerContext.ValueProviderFactories[1]);
        }

        [Fact]
        public async Task DeletingValueProviderFactory_AtResourceFilter_IsNotAvailableInPageContext()
        {
            // Arrange
            var resourceFilter = new Mock<IResourceFilter>();
            resourceFilter
                .Setup(f => f.OnResourceExecuting(It.IsAny<ResourceExecutingContext>()))
                .Callback<ResourceExecutingContext>((resourceExecutingContext) =>
                {
                    resourceExecutingContext.ValueProviderFactories.RemoveAt(0);
                });

            var valueProviderFactory1 = Mock.Of<IValueProviderFactory>();
            var valueProviderFactory2 = Mock.Of<IValueProviderFactory>();
            var valueProviderFactories = new List<IValueProviderFactory>();
            valueProviderFactories.Add(valueProviderFactory1);
            valueProviderFactories.Add(valueProviderFactory2);

            var invoker = CreateInvoker(
                new IFilterMetadata[] { resourceFilter.Object }, valueProviderFactories: valueProviderFactories);

            // Act
            await invoker.InvokeAsync();

            // Assert
            var controllerContext = Assert.IsType<PageActionInvoker>(invoker).PageContext;
            Assert.NotNull(controllerContext);
            Assert.Equal(1, controllerContext.ValueProviderFactories.Count);
            Assert.Same(valueProviderFactory2, controllerContext.ValueProviderFactories[0]);
        }

        #endregion

        #region Action Filters

        [Fact]
        public async Task InvokeAction_InvokesPageFilter()
        {
            // Arrange
            IActionResult result = null;

            var filter = new Mock<IPageFilter>(MockBehavior.Strict);
            filter.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            filter
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c => result = c.Result)
                .Verifiable();

            var invoker = CreateInvoker(filter.Object, result: Result);

            // Act
            await invoker.InvokeAsync();

            // Assert
            filter.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            filter.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            Assert.Same(Result, result);
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncActionFilter()
        {
            // Arrange
            IActionResult result = null;

            var filter = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            filter
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>(async (context, next) =>
                {
                    var resultContext = await next();
                    result = resultContext.Result;
                })
                .Verifiable();

            var invoker = CreateInvoker(filter.Object, result: Result);

            // Act
            await invoker.InvokeAsync();

            // Assert
            filter.Verify(
                f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()),
                Times.Once());

            Assert.Same(Result, result);
        }

        [Fact]
        public async Task InvokeAction_InvokesActionFilter_ShortCircuit()
        {
            // Arrange
            var result = new Mock<IActionResult>(MockBehavior.Strict);
            result
                .Setup(r => r.ExecuteResultAsync(It.IsAny<ActionContext>()))
                .Returns(Task.FromResult(true))
                .Verifiable();

            PageHandlerExecutedContext context = null;

            var actionFilter1 = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter1.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            actionFilter1
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c => context = c)
                .Verifiable();

            var actionFilter2 = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter2
                .Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()))
                .Callback<PageHandlerExecutingContext>(c => c.Result = result.Object)
                .Verifiable();

            var actionFilter3 = new Mock<IPageFilter>(MockBehavior.Strict);

            var resultFilter = new Mock<IResultFilter>(MockBehavior.Strict);
            resultFilter.Setup(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>())).Verifiable();
            resultFilter.Setup(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>())).Verifiable();

            var invoker = CreateInvoker(new IFilterMetadata[]
            {
                actionFilter1.Object,
                actionFilter2.Object,
                actionFilter3.Object,
                resultFilter.Object,
            });

            // Act
            await invoker.InvokeAsync();

            // Assert
            result.Verify(r => r.ExecuteResultAsync(It.IsAny<ActionContext>()), Times.Once());
            actionFilter1.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            actionFilter1.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            actionFilter2.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            actionFilter2.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Never());

            resultFilter.Verify(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>()), Times.Once());
            resultFilter.Verify(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>()), Times.Once());

            Assert.True(context.Canceled);
            Assert.Same(context.Result, result.Object);
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncActionFilter_ShortCircuit_WithResult()
        {
            // Arrange
            var result = new Mock<IActionResult>(MockBehavior.Strict);
            result
                .Setup(r => r.ExecuteResultAsync(It.IsAny<ActionContext>()))
                .Returns(Task.FromResult(true))
                .Verifiable();

            PageHandlerExecutedContext context = null;

            var actionFilter1 = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter1.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            actionFilter1
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c => context = c)
                .Verifiable();

            var actionFilter2 = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            actionFilter2
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>((c, next) =>
                {
                    // Notice we're not calling next
                    c.Result = result.Object;
                    return Task.FromResult(true);
                })
                .Verifiable();

            var actionFilter3 = new Mock<IPageFilter>(MockBehavior.Strict);

            var resultFilter1 = new Mock<IResultFilter>(MockBehavior.Strict);
            resultFilter1.Setup(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>())).Verifiable();
            resultFilter1.Setup(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>())).Verifiable();
            var resultFilter2 = new Mock<IResultFilter>(MockBehavior.Strict);
            resultFilter2.Setup(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>())).Verifiable();
            resultFilter2.Setup(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>())).Verifiable();

            var invoker = CreateInvoker(new IFilterMetadata[]
            {
                actionFilter1.Object,
                actionFilter2.Object,
                actionFilter3.Object,
                resultFilter1.Object,
                resultFilter2.Object,
            });

            // Act
            await invoker.InvokeAsync();

            // Assert
            result.Verify(r => r.ExecuteResultAsync(It.IsAny<ActionContext>()), Times.Once());
            actionFilter1.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            actionFilter1.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            actionFilter2.Verify(
                f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()),
                Times.Once());

            resultFilter1.Verify(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>()), Times.Once());
            resultFilter1.Verify(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>()), Times.Once());
            resultFilter2.Verify(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>()), Times.Once());
            resultFilter2.Verify(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>()), Times.Once());

            Assert.True(context.Canceled);
            Assert.Same(context.Result, result.Object);
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncActionFilter_ShortCircuit_WithoutResult()
        {
            // Arrange
            PageHandlerExecutedContext context = null;

            var actionFilter1 = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter1.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            actionFilter1
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c => context = c)
                .Verifiable();

            var actionFilter2 = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            actionFilter2
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>((c, next) =>
                {
                    // Notice we're not calling next
                    return Task.FromResult(true);
                })
                .Verifiable();

            var actionFilter3 = new Mock<IPageFilter>(MockBehavior.Strict);

            var resultFilter = new Mock<IResultFilter>(MockBehavior.Strict);
            resultFilter.Setup(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>())).Verifiable();
            resultFilter.Setup(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>())).Verifiable();

            var invoker = CreateInvoker(new IFilterMetadata[]
            {
                actionFilter1.Object,
                actionFilter2.Object,
                actionFilter3.Object,
                resultFilter.Object,
            });

            // Act
            await invoker.InvokeAsync();

            // Assert
            actionFilter1.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            actionFilter1.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            actionFilter2.Verify(
                f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()),
                Times.Once());

            resultFilter.Verify(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>()), Times.Once());
            resultFilter.Verify(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>()), Times.Once());

            Assert.True(context.Canceled);
            Assert.Null(context.Result);
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncActionFilter_ShortCircuit_WithResult_CallNext()
        {
            // Arrange
            var actionFilter = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            actionFilter
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>(async (c, next) =>
                {
                    c.Result = new EmptyResult();
                    await next();
                })
                .Verifiable();

            var message =
                "If an IAsyncPageFilter provides a result value by setting the Result property of " +
                "PageHandlerExecutingContext to a non-null value, then it cannot call the next filter by invoking " +
                "PageHandlerExecutionDelegate.";

            var invoker = CreateInvoker(actionFilter.Object);

            // Act & Assert
            await ExceptionAssert.ThrowsAsync<InvalidOperationException>(
                async () => await invoker.InvokeAsync(),
                message);
        }

        [Fact]
        public async Task InvokeAction_InvokesActionFilter_WithExceptionThrownByAction()
        {
            // Arrange
            PageHandlerExecutedContext context = null;

            var filter = new Mock<IPageFilter>(MockBehavior.Strict);
            filter.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            filter
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c =>
                {
                    context = c;

                    // Handle the exception so the test doesn't throw.
                    Assert.False(c.ExceptionHandled);
                    c.ExceptionHandled = true;
                })
                .Verifiable();

            var invoker = CreateInvoker(filter.Object, exception: Exception);

            // Act
            await invoker.InvokeAsync();

            // Assert
            filter.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            filter.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            Assert.Same(Exception, context.Exception);
            Assert.Null(context.Result);
        }

        [Fact]
        public async Task InvokeAction_InvokesActionFilter_WithExceptionThrownByActionFilter()
        {
            // Arrange
            var exception = new DataMisalignedException();
            PageHandlerExecutedContext context = null;

            var filter1 = new Mock<IPageFilter>(MockBehavior.Strict);
            filter1.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            filter1
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c =>
                {
                    context = c;

                    // Handle the exception so the test doesn't throw.
                    Assert.False(c.ExceptionHandled);
                    c.ExceptionHandled = true;
                })
                .Verifiable();

            var filter2 = new Mock<IPageFilter>(MockBehavior.Strict);
            filter2
                .Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()))
                .Callback<PageHandlerExecutingContext>(c => { throw exception; })
                .Verifiable();

            var invoker = CreateInvoker(new[] { filter1.Object, filter2.Object });

            // Act
            await invoker.InvokeAsync();

            // Assert
            filter1.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            filter1.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            filter2.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            filter2.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Never());

            Assert.Same(exception, context.Exception);
            Assert.Null(context.Result);
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncActionFilter_WithExceptionThrownByActionFilter()
        {
            // Arrange
            var exception = new DataMisalignedException();
            PageHandlerExecutedContext context = null;

            var filter1 = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            filter1
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>(async (c, next) =>
                {
                    context = await next();

                    // Handle the exception so the test doesn't throw.
                    Assert.False(context.ExceptionHandled);
                    context.ExceptionHandled = true;
                })
                .Verifiable();

            var filter2 = new Mock<IPageFilter>(MockBehavior.Strict);
            filter2.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            filter2
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c => { throw exception; })
                .Verifiable();

            var invoker = CreateInvoker(new IFilterMetadata[] { filter1.Object, filter2.Object });

            // Act
            await invoker.InvokeAsync();

            // Assert
            filter1.Verify(
                f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()),
                Times.Once());

            filter2.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());

            Assert.Same(exception, context.Exception);
            Assert.Null(context.Result);
        }

        [Fact]
        public async Task InvokeAction_InvokesActionFilter_HandleException()
        {
            // Arrange
            var result = new Mock<IActionResult>(MockBehavior.Strict);
            result
                .Setup(r => r.ExecuteResultAsync(It.IsAny<ActionContext>()))
                .Returns<ActionContext>((context) => Task.FromResult(true))
                .Verifiable();

            var actionFilter = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter.Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>())).Verifiable();
            actionFilter
                .Setup(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()))
                .Callback<PageHandlerExecutedContext>(c =>
                {
                    // Handle the exception so the test doesn't throw.
                    Assert.False(c.ExceptionHandled);
                    c.ExceptionHandled = true;

                    c.Result = result.Object;
                })
                .Verifiable();

            var resultFilter = new Mock<IResultFilter>(MockBehavior.Strict);
            resultFilter.Setup(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>())).Verifiable();
            resultFilter.Setup(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>())).Verifiable();

            var invoker = CreateInvoker(
                new IFilterMetadata[] { actionFilter.Object, resultFilter.Object },
                exception: Exception);

            // Act
            await invoker.InvokeAsync();

            // Assert
            actionFilter.Verify(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()), Times.Once());
            actionFilter.Verify(f => f.OnPageHandlerExecuted(It.IsAny<PageHandlerExecutedContext>()), Times.Once());

            resultFilter.Verify(f => f.OnResultExecuting(It.IsAny<ResultExecutingContext>()), Times.Once());
            resultFilter.Verify(f => f.OnResultExecuted(It.IsAny<ResultExecutedContext>()), Times.Once());

            result.Verify(r => r.ExecuteResultAsync(It.IsAny<ActionContext>()), Times.Once());
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncResourceFilter_WithActionResult_FromActionFilter()
        {
            // Arrange
            var expected = Mock.Of<IActionResult>();

            ResourceExecutedContext context = null;
            var resourceFilter = new Mock<IAsyncResourceFilter>(MockBehavior.Strict);
            resourceFilter
                .Setup(f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()))
                .Returns<ResourceExecutingContext, ResourceExecutionDelegate>(async (c, next) =>
                {
                    context = await next();
                })
                .Verifiable();

            var actionFilter = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter
                .Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()))
                .Callback<PageHandlerExecutingContext>((c) =>
                {
                    c.Result = expected;
                });

            var invoker = CreateInvoker(new IFilterMetadata[] { resourceFilter.Object, actionFilter.Object });

            // Act
            await invoker.InvokeAsync();

            // Assert
            Assert.Same(expected, context.Result);

            resourceFilter.Verify(
                f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()),
                Times.Once());
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncResourceFilter_HandleException_FromActionFilter()
        {
            // Arrange
            var expected = new DataMisalignedException();

            ResourceExecutedContext context = null;
            var resourceFilter = new Mock<IAsyncResourceFilter>(MockBehavior.Strict);
            resourceFilter
                .Setup(f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()))
                .Returns<ResourceExecutingContext, ResourceExecutionDelegate>(async (c, next) =>
                {
                    context = await next();
                    context.ExceptionHandled = true;
                })
                .Verifiable();

            var actionFilter = new Mock<IPageFilter>(MockBehavior.Strict);
            actionFilter
                .Setup(f => f.OnPageHandlerExecuting(It.IsAny<PageHandlerExecutingContext>()))
                .Callback<PageHandlerExecutingContext>((c) =>
                {
                    throw expected;
                });

            var invoker = CreateInvoker(new IFilterMetadata[] { resourceFilter.Object, actionFilter.Object });

            // Act
            await invoker.InvokeAsync();

            // Assert
            Assert.Same(expected, context.Exception);
            Assert.Same(expected, context.ExceptionDispatchInfo.SourceException);

            resourceFilter.Verify(
                f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()),
                Times.Once());
        }

        [Fact]
        public async Task InvokeAction_InvokesAsyncResourceFilter_HandlesException_FromExceptionFilter()
        {
            // Arrange
            var expected = new DataMisalignedException();

            ResourceExecutedContext context = null;
            var resourceFilter = new Mock<IAsyncResourceFilter>(MockBehavior.Strict);
            resourceFilter
                .Setup(f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()))
                .Returns<ResourceExecutingContext, ResourceExecutionDelegate>(async (c, next) =>
                {
                    context = await next();
                    context.ExceptionHandled = true;
                })
                .Verifiable();

            var exceptionFilter = new Mock<IExceptionFilter>(MockBehavior.Strict);
            exceptionFilter
                .Setup(f => f.OnException(It.IsAny<ExceptionContext>()))
                .Callback<ExceptionContext>((c) =>
                {
                    throw expected;
                });

            var invoker = CreateInvoker(new IFilterMetadata[] { resourceFilter.Object, exceptionFilter.Object }, exception: Exception);

            // Act
            await invoker.InvokeAsync();

            // Assert
            Assert.Same(expected, context.Exception);
            Assert.Same(expected, context.ExceptionDispatchInfo.SourceException);

            resourceFilter.Verify(
                f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()),
                Times.Once());
        }

        [Fact]
        public async Task InvokeAction_ExceptionBubbling_AsyncActionFilter_To_ResourceFilter()
        {
            // Arrange
            var resourceFilter = new Mock<IAsyncResourceFilter>(MockBehavior.Strict);
            resourceFilter
                .Setup(f => f.OnResourceExecutionAsync(It.IsAny<ResourceExecutingContext>(), It.IsAny<ResourceExecutionDelegate>()))
                .Returns<ResourceExecutingContext, ResourceExecutionDelegate>(async (c, next) =>
                {
                    var context = await next();
                    Assert.Same(Exception, context.Exception);
                    context.ExceptionHandled = true;
                });

            var actionFilter1 = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            actionFilter1
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>(async (c, next) =>
                {
                    await next();
                });

            var actionFilter2 = new Mock<IAsyncPageFilter>(MockBehavior.Strict);
            actionFilter2
                .Setup(f => f.OnPageHandlerExecutionAsync(It.IsAny<PageHandlerExecutingContext>(), It.IsAny<PageHandlerExecutionDelegate>()))
                .Returns<PageHandlerExecutingContext, PageHandlerExecutionDelegate>(async (c, next) =>
                {
                    await next();
                });

            var invoker = CreateInvoker(
                new IFilterMetadata[]
                {
                    resourceFilter.Object,
                    actionFilter1.Object,
                    actionFilter2.Object,
                },
                // The action won't run
                exception: Exception);

            // Act & Assert
            await invoker.InvokeAsync();
        }

        #endregion

        protected override ResourceInvoker CreateInvoker(
            IFilterMetadata[] filters,
            Exception exception = null,
            IActionResult result = null,
            IList<IValueProviderFactory> valueProviderFactories = null)
        {
            var actionDescriptor = new CompiledPageActionDescriptor
            {
                ViewEnginePath = "/Index.cshtml",
                RelativePath = "/Index.cshtml",
                HandlerMethods = new List<HandlerMethodDescriptor>(),
                HandlerTypeInfo = typeof(TestPage).GetTypeInfo(),
                ModelTypeInfo = typeof(TestPage).GetTypeInfo(),
                PageTypeInfo = typeof(TestPage).GetTypeInfo(),
            };

            var handlers = new List<Func<object, object[], Task<IActionResult>>>();
            if (result != null)
            {
                handlers.Add((obj, args) => Task.FromResult(result));
                actionDescriptor.HandlerMethods.Add(new HandlerMethodDescriptor()
                {
                    HttpMethod = "GET",
                    Parameters = new List<HandlerParameterDescriptor>(),
                });
            }
            else if (exception != null)
            {
                handlers.Add((obj, args) => Task.FromException<IActionResult>(exception));
                actionDescriptor.HandlerMethods.Add(new HandlerMethodDescriptor()
                {
                    HttpMethod = "GET",
                    Parameters = new List<HandlerParameterDescriptor>(),
                });
            }

            var executor = new TestPageResultExecutor();
            return CreateInvoker(
                filters,
                actionDescriptor,
                executor,
                handlers: handlers.ToArray());
        }

        private PageActionInvoker CreateInvoker(
            IFilterMetadata[] filters,
            CompiledPageActionDescriptor actionDescriptor,
            PageResultExecutor executor = null,
            PageActionInvokerCacheEntry cacheEntry = null,
            ITempDataDictionaryFactory tempDataFactory = null,
            IList<IValueProviderFactory> valueProviderFactories = null,
            Func<object, object[], Task<IActionResult>>[] handlers = null,
            RouteData routeData = null,
            ILogger logger = null,
            TestDiagnosticListener listener = null)
        {
            var diagnosticListener = new DiagnosticListener("Microsoft.AspNetCore");
            if (listener != null)
            {
                diagnosticListener.SubscribeWithAdapter(listener);
            }

            var httpContext = new DefaultHttpContext();
            var services = new ServiceCollection();
            if (executor == null)
            {
                executor = new PageResultExecutor(
                    Mock.Of<IHttpResponseStreamWriterFactory>(),
                    Mock.Of<ICompositeViewEngine>(),
                    Mock.Of<IRazorViewEngine>(),
                    Mock.Of<IRazorPageActivator>(),
                    diagnosticListener,
                    HtmlEncoder.Default);
            }

            services.AddSingleton(executor);
            httpContext.RequestServices = services.BuildServiceProvider();

            var pageContext = new PageContext()
            {
                ActionDescriptor = actionDescriptor,
                HttpContext = httpContext,
                RouteData = routeData ?? new RouteData(),
            };

            var viewDataFactory = ViewDataDictionaryFactory.CreateFactory(actionDescriptor.ModelTypeInfo);
            pageContext.ViewData = viewDataFactory(new EmptyModelMetadataProvider(), pageContext.ModelState);

            if (tempDataFactory == null)
            {
                tempDataFactory = Mock.Of<ITempDataDictionaryFactory>(m => m.GetTempData(It.IsAny<HttpContext>()) == Mock.Of<ITempDataDictionary>());
            }

            Func<PageContext, ViewContext, object> pageFactory = (context, viewContext) =>
            {
                var instance = (Page)Activator.CreateInstance(actionDescriptor.PageTypeInfo.AsType());
                instance.PageContext = context;
                return instance;
            };

            cacheEntry = new PageActionInvokerCacheEntry(
                actionDescriptor,
                viewDataFactory,
                pageFactory,
                (c, viewContext, page) => { (page as IDisposable)?.Dispose(); },
                _ => Activator.CreateInstance(actionDescriptor.ModelTypeInfo.AsType()),
                (c, model) => { (model as IDisposable)?.Dispose(); },
                null,
                handlers,
                null,
                new FilterItem[0]);

            // Always just select the first one.
            var selector = new Mock<IPageHandlerMethodSelector>();
            selector
                .Setup(s => s.Select(It.IsAny<PageContext>()))
                .Returns<PageContext>(c => c.ActionDescriptor.HandlerMethods.FirstOrDefault());
            
            var invoker = new PageActionInvoker(
                selector.Object,
                diagnosticListener ?? new DiagnosticListener("Microsoft.AspNetCore"),
                logger ?? NullLogger.Instance,
                pageContext,
                filters ?? Array.Empty<IFilterMetadata>(),
                valueProviderFactories?.ToArray() ?? Array.Empty<IValueProviderFactory>(),
                cacheEntry,
                GetParameterBinder(),
                tempDataFactory,
                new HtmlHelperOptions());
            return invoker;
        }

        private static ParameterBinder GetParameterBinder(
            IModelBinderFactory factory = null,
            IObjectModelValidator validator = null)
        {
            if (validator == null)
            {
                validator = CreateMockValidator();
            }

            if (factory == null)
            {
                factory = TestModelBinderFactory.CreateDefault();
            }

            return new ParameterBinder(
                TestModelMetadataProvider.CreateDefaultProvider(),
                factory,
                validator);
        }

        private static IObjectModelValidator CreateMockValidator()
        {
            var mockValidator = new Mock<IObjectModelValidator>(MockBehavior.Strict);
            mockValidator
                .Setup(o => o.Validate(
                    It.IsAny<ActionContext>(),
                    It.IsAny<ValidationStateDictionary>(),
                    It.IsAny<string>(),
                    It.IsAny<object>()));
            return mockValidator.Object;
        }

        private CompiledPageActionDescriptor CreateDescriptorForSimplePage()
        {
            return new CompiledPageActionDescriptor()
            {
                HandlerTypeInfo = typeof(TestPage).GetTypeInfo(),
                ModelTypeInfo = typeof(TestPage).GetTypeInfo(),
                PageTypeInfo = typeof(TestPage).GetTypeInfo(),

                HandlerMethods = new HandlerMethodDescriptor[]
                {
                    new HandlerMethodDescriptor()
                    {
                        HttpMethod = "GET",
                        MethodInfo = typeof(TestPage).GetTypeInfo().GetMethod(nameof(TestPage.OnGetHandler1)),
                    },
                    new HandlerMethodDescriptor()
                    {
                        HttpMethod = "GET",
                        MethodInfo = typeof(TestPage).GetTypeInfo().GetMethod(nameof(TestPage.OnGetHandler2)),
                    },
                },
            };
        }

        private CompiledPageActionDescriptor CreateDescriptorForSimplePageWithPocoModel()
        {
            return new CompiledPageActionDescriptor()
            {
                HandlerTypeInfo = typeof(TestPage).GetTypeInfo(),
                ModelTypeInfo = typeof(PocoModel).GetTypeInfo(),
                PageTypeInfo = typeof(TestPage).GetTypeInfo(),

                HandlerMethods = new HandlerMethodDescriptor[]
                {
                    new HandlerMethodDescriptor()
                    {
                        HttpMethod = "GET",
                        MethodInfo = typeof(TestPage).GetTypeInfo().GetMethod(nameof(TestPage.OnGetHandler1)),
                    },
                    new HandlerMethodDescriptor()
                    {
                        HttpMethod = "GET",
                        MethodInfo = typeof(TestPage).GetTypeInfo().GetMethod(nameof(TestPage.OnGetHandler2)),
                    },
                },
            };
        }

        private CompiledPageActionDescriptor CreateDescriptorForPageModelPage()
        {
            return new CompiledPageActionDescriptor()
            {
                HandlerTypeInfo = typeof(TestPageModel).GetTypeInfo(),
                ModelTypeInfo = typeof(TestPageModel).GetTypeInfo(),
                PageTypeInfo = typeof(TestPage).GetTypeInfo(),

                HandlerMethods = new HandlerMethodDescriptor[]
                {
                    new HandlerMethodDescriptor()
                    {
                        HttpMethod = "GET",
                        MethodInfo = typeof(PageModel).GetTypeInfo().GetMethod(nameof(TestPageModel.OnGetHandler1)),
                    },
                    new HandlerMethodDescriptor()
                    {
                        HttpMethod = "GET",
                        MethodInfo = typeof(PageModel).GetTypeInfo().GetMethod(nameof(TestPageModel.OnGetHandler2)),
                    },
                },
            };
        }

        private class TestPageResultExecutor : PageResultExecutor
        {
            private readonly Func<PageContext, Task> _executeAction;

            public TestPageResultExecutor()
                : this(null)
            {
            }

            public TestPageResultExecutor(Func<PageContext, Task> executeAction)
                : base(
                    Mock.Of<IHttpResponseStreamWriterFactory>(),
                    Mock.Of<ICompositeViewEngine>(),
                    Mock.Of<IRazorViewEngine>(),
                    Mock.Of<IRazorPageActivator>(),
                    new DiagnosticListener("Microsoft.AspNetCore"),
                    HtmlEncoder.Default)
            {
                _executeAction = executeAction;
            }

            public override Task ExecuteAsync(PageContext pageContext, PageResult result)
            {
                return _executeAction?.Invoke(pageContext) ?? Task.CompletedTask;
            }
        }

        private class PocoModel
        {
        }

        private class TestPage : Page
        {
            public void OnGetHandler1()
            {
            }

            public void OnGetHandler2()
            {
            }

            public override Task ExecuteAsync()
            {
                throw new NotImplementedException();
            }
        }

        private class TestPageModel : PageModel
        {
            public void OnGetHandler1()
            {
            }

            public void OnGetHandler2()
            {
            }
        }
    }
}
