
using Gommon;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems
{
    internal static partial class Updater
    {
        private const string UpdateEndpoint = "https://thebeastroofer-edition.com/updates/latest";

        private static string _artifactUrl;

        private sealed class BeastUpdateInfo
        {
            [JsonPropertyName("version")] public string Version { get; set; }
            [JsonPropertyName("url")] public string Url { get; set; }
            [JsonPropertyName("notes")] public string Notes { get; set; }
        }

        public static async Task<Optional<(Version Current, Version Incoming)>> CheckVersionAsync(bool showVersionUpToDate = false)
        {
            if (!Version.TryParse(Program.Version, out Version currentVersion))
            {
                Logger.Error?.Print(LogClass.Application,
                    $"Failed to convert the current {RyujinxApp.FullAppName} version!");

                await ContentDialogHelper.CreateWarningDialog(
                    LocaleManager.Instance[LocaleKeys.DialogUpdaterConvertFailedMessage],
                    LocaleManager.Instance[LocaleKeys.DialogUpdaterCancelUpdateMessage]);

                _running = false;

                return default;
            }

            BeastUpdateInfo info;

            try
            {
                using HttpClient client = ConstructHttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                info = await client.GetFromJsonAsync<BeastUpdateInfo>(UpdateEndpoint);
            }
            catch (Exception e)
            {
                Logger.Error?.Print(LogClass.Application,
                    $"{e.GetType().AsPrettyString()} thrown when requesting updates: {e.Message}");

                _running = false;

                return default;
            }

            if (info == null || string.IsNullOrEmpty(info.Url))
            {
                if (showVersionUpToDate)
                {
                    await ContentDialogHelper.CreateUpdaterUpToDateInfoDialog(
                        LocaleManager.Instance[LocaleKeys.DialogUpdaterAlreadyOnLatestVersionMessage],
                        string.Empty,
                        ReleaseInformation.GetChangelogUrl(currentVersion, currentVersion));
                }

                Logger.Info?.Print(LogClass.Application, "Up to date.");

                _running = false;

                return default;
            }

            if (!Version.TryParse(info.Version, out Version newVersion))
            {
                Logger.Error?.Print(LogClass.Application,
                    $"Failed to convert the received {RyujinxApp.FullAppName} version from the update server!");

                await ContentDialogHelper.CreateWarningDialog(
                    LocaleManager.Instance[LocaleKeys.DialogUpdaterConvertFailedServerMessage],
                    LocaleManager.Instance[LocaleKeys.DialogUpdaterCancelUpdateMessage]);

                _running = false;

                return default;
            }

            _artifactUrl = info.Url;

            _connectionCount = 1;

            return (currentVersion, newVersion);
        }
    }
}
