using System;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Injects the Allocine script into the HTML using Reflection to handle JObject safely.
    /// </summary>
    public static class HtmlInjector
    {
        /// <summary>
        /// Injects the script into the page contents.
        /// </summary>
        /// <param name="model">The model containing the page contents (JObject).</param>
        /// <returns>The modified HTML content, or null if injection failed.</returns>
        public static string? Inject(object model)
        {
            try
            {
                var type = model.GetType();

                var itemProperty = type.GetProperty("Item", new[] { typeof(string) });
                if (itemProperty == null)
                {
                    return null;
                }

                var contentObj = itemProperty.GetValue(model, new object[] { "contents" });
                var content = contentObj?.ToString();

                if (string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }

                string scriptTag = "<script src=\"/Allocine/Script\" defer></script>";

                if (content.Contains("src=\"/Allocine/Script\"", StringComparison.OrdinalIgnoreCase))
                {
                    return content;
                }

                string newContent;
                if (content.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                {
                    newContent = content.Replace("</body>", $"{scriptTag}</body>", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    return content;
                }

                return newContent;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Allocine] Critical Injection Error: {ex}");
                return null;
            }
        }
    }
}
