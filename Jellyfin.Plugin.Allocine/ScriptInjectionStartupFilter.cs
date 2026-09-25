using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Injects the Allociné client script into Jellyfin Web index responses.
    /// </summary>
    public sealed class ScriptInjectionStartupFilter : IStartupFilter
    {
        private const string ScriptPath = "/Allocine/Script";

        private readonly ILogger<ScriptInjectionStartupFilter> _logger;
        private int _loggedOnce;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(InvokeAsync);
                next(app);
            };
        }

        internal static bool IsIndexRequest(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.Equals("/web", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
        }

        internal static string InjectScript(string html)
        {
            return InjectScript(html, string.Empty);
        }

        internal static string InjectScript(string html, string pathBase)
        {
            if (html.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase))
            {
                return html;
            }

            int bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            string normalizedPathBase = pathBase == "/" ? string.Empty : pathBase.TrimEnd('/');
            string scriptTag = $"<script src=\"{normalizedPathBase}{ScriptPath}?v=0.5.3\" defer></script>";
            return bodyEnd < 0
                ? html
                : string.Concat(html.AsSpan(0, bodyEnd), scriptTag, "\n", html.AsSpan(bodyEnd));
        }

        private async Task InvokeAsync(HttpContext context, Func<Task> next)
        {
            if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexRequest(context.Request.Path.Value))
            {
                await next().ConfigureAwait(false);
                return;
            }

            context.Request.Headers.Remove("Accept-Encoding");
            context.Request.Headers.Remove("Range");
            context.Request.Headers.Remove("If-Range");

            Stream originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await next().ConfigureAwait(false);
            }
            catch
            {
                context.Response.Body = originalBody;
                throw;
            }

            context.Response.Body = originalBody;
            buffer.Position = 0;

            if (context.Response.StatusCode != StatusCodes.Status200OK
                || !(context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            string html;
            using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, true))
            {
                html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            }

            string injected = InjectScript(html, context.Request.PathBase.ToUriComponent());
            if (!ReferenceEquals(injected, html) && Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            {
                _logger.LogInformation("[Allocine] Client script injected through the Jellyfin 12 request pipeline.");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(injected);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.Remove("Accept-Ranges");
            await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
