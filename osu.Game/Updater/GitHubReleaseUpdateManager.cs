// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform;
using osu.Game.Online.API;
using osu.Game.Overlays.Notifications;

namespace osu.Game.Updater
{
    /// <summary>
    /// An update manager that shows notifications if a newer release is detected.
    /// This is a case where updates are handled externally by a package manager or other means, so no action is performed on clicking the notification.
    /// </summary>
    public partial class GitHubReleaseUpdateManager : UpdateManager
    {
        private string version = null!;

        [Resolved]
        private GameHost host { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load(OsuGameBase game)
        {
            version = game.Version;
        }

        protected override async Task<bool> PerformUpdateCheck()
        {
            try
            {
                var releases = new OsuJsonWebRequest<GitHubRelease>(@"https://api.github.com/repos/ILW8/osu-tclient/releases/latest");

                await releases.PerformAsync().ConfigureAwait(false);

                var latest = releases.ResponseObject;

                if (latest == null)
                    return false;

                // avoid any discrepancies due to build suffixes for now.
                // eventually we will want to support release streams and consider these.
                version = version.Split('-').First();
                string latestTagName = latest.TagName.Split('-').First();

                if (latestTagName != version)
                {
                    Notifications.Post(new SimpleNotification
                    {
                        Text = $"A newer release of osu! has been found ({version} → {latestTagName}).\n\n"
                               + "Click here to open the downloads page!",
                        Icon = FontAwesome.Solid.Download,
                        Activated = () =>
                        {
                            host.OpenUrlExternally(latest.HtmlUrl);
                            return true;
                        }
                    });

                    return true;
                }
            }
            catch
            {
                // we shouldn't crash on a web failure. or any failure for the matter.
                return true;
            }

            return false;
        }
    }
}
