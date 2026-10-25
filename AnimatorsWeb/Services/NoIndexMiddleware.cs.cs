using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace AnimatorsWeb.Services
{
    public class NoIndexMiddleware
    {
        private readonly RequestDelegate _next;

        public NoIndexMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";

            bool isAllowed =
                path == "/" ||
                path.Equals("/sitemap.xml", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/Featured/Thumbnail/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/Media/StreamWork/", StringComparison.OrdinalIgnoreCase);

            if (!isAllowed)
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex";
            }

            await _next(context);
        }
    }
}