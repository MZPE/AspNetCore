// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server.Prerendering;
using Microsoft.AspNetCore.Components.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.Components.Server.Tests.Circuits
{
    public class CircuitPrerendererTest
    {
        // FOR NOW: The circuit prerenderer will wrap the content inside a div tag with a few data attributes
        // data-circuit-id data-renderer-id data-component-id that are used for reconnecting to the running
        // prerendered components.
        // In the future we will lift this limitation by emiting comment nodes instead 
        // <!-- M.A.C START circuit-id="..." renderer-id="..." component-id="..." --!> <!-- M.A.C END --!>
        // Currently to render an interactive component or to prerender one and reconnect you need to provide a selector
        // and an HTML tag in the document for the component to be plugged-in to, so we are not removing any existing capability
        // by taking over the rendered element.
        private static readonly Regex ContentWrapperRegex = new Regex(
            "^<div data-circuit-id=\"[^\"]+\" data-renderer-id=\"0\" data-component-id=\"0\">(?<content>.*)</div>$",
            RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1)); // Treat the entire input string as a single line

        // Because CircuitPrerenderer is a point of integration with HttpContext,
        // it's not a good candidate for unit testing. The majority of prerendering
        // unit tests should be elsewhere in HtmlRendererTests inside the
        // Microsoft.AspNetCore.Components.Tests projects.
        //
        // The only unit tests added here should specifically be about how we're
        // interacting with the HttpContext for configuring the prerenderer.

        [Fact]
        public async Task ExtractsUriFromHttpContext_EmptyPathBase()
        {
            // Arrange
            var circuitFactory = new TestCircuitFactory();
            var circuitRegistry = new CircuitRegistry(Options.Create(new CircuitOptions()), Mock.Of<ILogger<CircuitRegistry>>());
            var circuitPrerenderer = new CircuitPrerenderer(circuitFactory, circuitRegistry);
            var httpContext = new DefaultHttpContext();
            var httpRequest = httpContext.Request;
            httpRequest.Scheme = "https";
            httpRequest.Host = new HostString("example.com", 1234);
            httpRequest.Path = "/some/path";

            var prerenderingContext = new ComponentPrerenderingContext
            {
                ComponentType = typeof(UriDisplayComponent),
                Parameters = ParameterCollection.Empty,
                Context = httpContext
            };

            // Act
            var result = await circuitPrerenderer.PrerenderComponentAsync(prerenderingContext);
            // Assert
            Assert.Equal(string.Join("", new[]
            {
                "The current URI is ",
                "https://example.com:1234/some/path",
                " within base URI ",
                "https://example.com:1234/"
            }), GetUnwrappedContent(result));
        }

        private string GetUnwrappedContent(ComponentPrerenderResult rawResult)
        {
            var writer = new StringWriter();
            rawResult.WriteTo(writer);
            return ContentWrapperRegex.Match(writer.ToString())
                .Groups["content"].Value
                .Replace("\r\n","\n");
        }

        [Fact]
        public async Task ExtractsUriFromHttpContext_NonemptyPathBase()
        {
            // Arrange
            var circuitFactory = new TestCircuitFactory();
            var circuitRegistry = new CircuitRegistry(Options.Create(new CircuitOptions()), Mock.Of<ILogger<CircuitRegistry>>());
            var circuitPrerenderer = new CircuitPrerenderer(circuitFactory, circuitRegistry);
            var httpContext = new DefaultHttpContext();
            var httpRequest = httpContext.Request;
            httpRequest.Scheme = "https";
            httpRequest.Host = new HostString("example.com", 1234);
            httpRequest.PathBase = "/my/dir";
            httpRequest.Path = "/some/path";

            var prerenderingContext = new ComponentPrerenderingContext
            {
                ComponentType = typeof(UriDisplayComponent),
                Parameters = ParameterCollection.Empty,
                Context = httpContext
            };

            // Act
            var result = await circuitPrerenderer.PrerenderComponentAsync(prerenderingContext);

            // Assert
            Assert.Equal(string.Join("", new[]
            {
                "The current URI is ",
                "https://example.com:1234/my/dir/some/path",
                " within base URI ",
                "https://example.com:1234/my/dir/"
            }), GetUnwrappedContent(result));
        }

        class TestCircuitFactory : CircuitFactory
        {
            public override CircuitHost CreateCircuitHost(HttpContext httpContext, CircuitClientProxy client, string uriAbsolute, string baseUriAbsolute)
            {
                var serviceCollection = new ServiceCollection();
                serviceCollection.AddScoped<IUriHelper>(_ =>
                {
                    var uriHelper = new RemoteUriHelper();
                    uriHelper.InitializeState(uriAbsolute, baseUriAbsolute);
                    return uriHelper;
                });
                var serviceScope = serviceCollection.BuildServiceProvider().CreateScope();
                return TestCircuitHost.Create(serviceScope);
            }
        }

        class UriDisplayComponent : IComponent
        {
            private RenderHandle _renderHandle;

            [Inject] IUriHelper UriHelper { get; set; }

            public void Configure(RenderHandle renderHandle)
            {
                _renderHandle = renderHandle;
            }

            public Task SetParametersAsync(ParameterCollection parameters)
            {
                _renderHandle.Render(builder =>
                {
                    builder.AddContent(0, "The current URI is ");
                    builder.AddContent(1, UriHelper.GetAbsoluteUri());
                    builder.AddContent(2, " within base URI ");
                    builder.AddContent(3, UriHelper.GetBaseUri());
                });

                return Task.CompletedTask;
            }
        }
    }
}
