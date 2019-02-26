// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Prerendering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Microsoft.AspNetCore.Components.Server.Circuits
{
    internal class CircuitPrerenderer : IComponentPrerenderer
    {
        private static object CircuitHostKey = new object();

        private readonly CircuitFactory _circuitFactory;

        public CircuitPrerenderer(CircuitFactory circuitFactory)
        {
            _circuitFactory = circuitFactory;
        }

        public async Task<ComponentPrerenderResult> PrerenderComponentAsync(ComponentPrerenderingContext prerenderingContext)
        {
            var context = prerenderingContext.Context;
            var circuitHost = GetOrCreateCircuitHost(context);

            try
            {
                circuitHost.Renderer.UnhandledException += PrerenderException;
                circuitHost.Renderer.UnhandledSynchronizationException += PrerenderUnhandledException;


            // For right now we just do prerendering and dispose the circuit. In the future we will keep the circuit around and
            // reconnect to it from the ComponentsHub.
                var renderResult = await circuitHost.PrerenderComponentAsync(
                    prerenderingContext.ComponentType,
                    prerenderingContext.Parameters);

                return new ComponentPrerenderResult(renderResult);
        }

        private CircuitHost GetOrCreateCircuitHost(HttpContext context)
        {
            if(context.Items.TryGetValue(CircuitHostKey, out var existingHost))
            {
                return (CircuitHost)existingHost;
            }
            else
            {
                var result = _circuitFactory.CreateCircuitHost(
                    context,
                    client: CircuitClientProxy.OfflineClient,
                    GetFullUri(context.Request),
                    GetFullBaseUri(context.Request));

                circuitHost.UnhandledException += CircuitHost_UnhandledException;

                context.Items.Add(CircuitHostKey, result);
                context.Response.RegisterForDisposeAsync(result);

                return result;
            }
        }

        private void CircuitHost_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Throw all exceptions encountered during pre-rendering so the default developer
            // error page can respond.
            ExceptionDispatchInfo.Capture((Exception)e.ExceptionObject).Throw();
        }

        private string GetFullUri(HttpRequest request)
        {
            return UriHelper.BuildAbsolute(
                request.Scheme,
                request.Host,
                request.PathBase,
                request.Path,
                request.QueryString);
        }

        private string GetFullBaseUri(HttpRequest request)
        {
            var result = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase);

            // PathBase may be "/" or "/some/thing", but to be a well-formed base URI
            // it has to end with a trailing slash
            if (!result.EndsWith('/'))
            {
                result += '/';
            }

            return result;
        }
    }
}
