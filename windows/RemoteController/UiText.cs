using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteController
{
    internal static class UiText
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Catalog = Load();
        private static readonly Dictionary<string, string> Empty = new Dictionary<string, string>();
#if STORE
        private const string Fallback = "uk";
#else
        private const string Fallback = "ru";
#endif

        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Web.locales.json"))
            using (var reader = new StreamReader(stream))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd());
        }

        public static string Language(string preferences)
        {
            if (string.IsNullOrWhiteSpace(preferences)) return Fallback;
            return preferences.Split(',').Select(value => value.Split(';')).Select(parts =>
            {
                double quality = 1;
                if (parts.Length > 1 && parts[1].Trim().StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(parts[1].Trim().Substring(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality);
                return new { Language = parts[0].Trim().Split('-')[0].ToLowerInvariant(), Quality = quality };
            }).Where(value => value.Quality > 0 && (value.Language == "ru" || Catalog.ContainsKey(value.Language)))
                .OrderByDescending(value => value.Quality).Select(value => value.Language).FirstOrDefault() ?? Fallback;
        }

        public static string Get(string text) { return Translate(text, Language(CultureInfo.CurrentUICulture.Name + "," + CultureInfo.CurrentCulture.Name)); }

        public static string Translate(string text, string language)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            string key = text.Trim(), translated;
            if (!Dictionary(language).TryGetValue(key, out translated)) return text;
            int start = text.IndexOf(key, StringComparison.Ordinal);
            return text.Substring(0, start) + translated + text.Substring(start + key.Length);
        }

        public static string BrowserScript()
        {
            return "window.RemoteTranslate = (function (catalog) { return function (text) { var language = document.documentElement.lang; var dictionary = catalog[language]; var key = typeof text === 'string' ? text.trim() : ''; return dictionary && Object.prototype.hasOwnProperty.call(dictionary, key) ? text.replace(key, function () { return dictionary[key]; }) : text; }; })(" +
                new JavaScriptSerializer().Serialize(Catalog) + ");";
        }

        public static string Html(string html, string language)
        {
            if (language == "ru") return html;
            html = Regex.Replace(html, "<html lang=(?:\"ru\"|ru)>", "<html lang=\"" + language + "\">");
            html = Regex.Replace(html, "(?<=>)[^<>]+(?=<)", match => HtmlText(match.Value, language));
            return Regex.Replace(html, "(\\b(?:aria-label|placeholder|alt|value)=\")([^\"]*)(\")", match =>
                match.Groups[1].Value + HtmlText(match.Groups[2].Value, language) + match.Groups[3].Value);
        }

        private static string HtmlText(string value, string language)
        {
            string decoded = WebUtility.HtmlDecode(value);
            string translated = Translate(decoded, language);
            return decoded == translated ? value : WebUtility.HtmlEncode(translated);
        }

        private static Dictionary<string, string> Dictionary(string language)
        {
            Dictionary<string, string> dictionary;
            return Catalog.TryGetValue(language, out dictionary) ? dictionary : Empty;
        }
    }
}
