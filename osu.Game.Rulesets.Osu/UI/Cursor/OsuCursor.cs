// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Logging;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Rulesets.Osu.Skinning;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.Osu.UI.Cursor
{
    public partial class OsuCursor : SkinReloadableDrawable
    {
        public const float SIZE = 28;

        private bool cursorExpand;

        private SkinnableDrawable cursorSprite;
        private Container cursorScaleContainer = null!;

        private SkinnableCursor skinnableCursor => (SkinnableCursor)cursorSprite.Drawable;

        /// <summary>
        /// The current expanded scale of the cursor.
        /// </summary>
        public Vector2 CurrentExpandedScale => skinnableCursor.ExpandTarget?.Scale ?? Vector2.One;

        /// <summary>
        /// The current rotation of the cursor.
        /// </summary>
        public float CurrentRotation => skinnableCursor.ExpandTarget?.Rotation ?? 0;

        public IBindable<float> CursorScale => cursorScale;

        /// <summary>
        /// Mods which want to adjust cursor size should do so via this bindable.
        /// </summary>
        public readonly Bindable<float> ModScaleAdjust = new Bindable<float>(1);

        private readonly Bindable<float> cursorScale = new BindableFloat(1);

        private Bindable<float> userCursorScale = null!;
        private Bindable<bool> autoCursorScale = null!;

        [Resolved(canBeNull: true)]
        private GameplayState state { get; set; }

        [Resolved]
        private OsuConfigManager config { get; set; }

        public OsuCursor()
        {
            Origin = Anchor.Centre;

            Size = new Vector2(SIZE);
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = CreateCursorContent();

            userCursorScale = config.GetBindable<float>(OsuSetting.GameplayCursorSize);
            userCursorScale.ValueChanged += _ => cursorScale.Value = CalculateCursorScale();

            autoCursorScale = config.GetBindable<bool>(OsuSetting.AutoCursorSize);
            autoCursorScale.ValueChanged += _ => cursorScale.Value = CalculateCursorScale();

            ModScaleAdjust.ValueChanged += _ => cursorScale.Value = CalculateCursorScale();

            cursorScale.BindValueChanged(e => cursorScaleContainer.Scale = new Vector2(e.NewValue), true);

            // [cursor-size] diagnostics. subscribed after the handlers above so the logged scale is the recomputed one.
            userCursorScale.ValueChanged += _ => logScale("user cursor size changed");
            autoCursorScale.ValueChanged += _ => logScale("auto cursor size changed");
            ModScaleAdjust.ValueChanged += _ => logScale("mod scale adjust changed");

            // the initial skin lookup already ran synchronously while CreateCursorContent() was loaded above,
            // so this only catches later skin changes. the initial state is logged in LoadComplete().
            cursorSprite.OnSkinChanged += () => logSkin("skin changed");
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            cursorScale.Value = CalculateCursorScale();

            logScale("load");
            logSkin("load");
        }

        protected override void Update()
        {
            base.Update();
            checkOnScreenSize();
        }

        protected virtual Drawable CreateCursorContent() => cursorScaleContainer = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Origin = Anchor.Centre,
            Anchor = Anchor.Centre,
            Child = cursorSprite = new SkinnableDrawable(new OsuSkinComponentLookup(OsuSkinComponents.Cursor), _ => new DefaultCursor(), confineMode: ConfineMode.NoScaling)
            {
                Origin = Anchor.Centre,
                Anchor = Anchor.Centre,
            },
        };

        protected virtual float CalculateCursorScale()
        {
            float scale = userCursorScale.Value * ModScaleAdjust.Value;

            if (autoCursorScale.Value && state != null)
            {
                // if we have a beatmap available, let's get its circle size to figure out an automatic cursor scale modifier.
                scale *= GetScaleForCircleSize(state.Beatmap.Difficulty.CircleSize);
            }

            return scale;
        }

        protected override void SkinChanged(ISkinSource skin)
        {
            cursorExpand = skin.GetConfig<OsuSkinConfiguration, bool>(OsuSkinConfiguration.CursorExpand)?.Value ?? true;
        }

        public void Expand()
        {
            if (++expandCount <= max_expand_logs)
                LogCursorSize($"Expand #{expandCount} (CursorExpand={cursorExpand}, expand target scale {(cursorSprite.Drawable as SkinnableCursor)?.ExpandTarget?.Scale.X:0.###})");

            if (!cursorExpand) return;

            skinnableCursor.Expand();
        }

        public void Contract()
        {
            if (++contractCount <= max_expand_logs)
                LogCursorSize($"Contract #{contractCount} (expand target scale {(cursorSprite.Drawable as SkinnableCursor)?.ExpandTarget?.Scale.X:0.###})");

            skinnableCursor.Contract();
        }

        #region [cursor-size] diagnostics (ppy/osu#14388)

        private const int max_scale_logs = 30;
        private const int max_size_logs = 40;
        private const int max_expand_logs = 2;

        private static readonly Stopwatch debug_stopwatch = Stopwatch.StartNew();
        private static int debugInstanceCount;

        private readonly int debugId = Interlocked.Increment(ref debugInstanceCount);

        private string lastScaleLog;
        private int scaleLogCount;

        private int expandCount;
        private int contractCount;

        private Drawable debugTile;
        private Vector2 candidateSize = new Vector2(-1);
        private double candidateSince;
        private Vector2? loggedSize;
        private bool loggedStretched;
        private int sizeLogCount;

        /// <summary>
        /// Logs a <c>[cursor-size]</c> line tagged with this cursor instance and the spectated user.
        /// </summary>
        internal void LogCursorSize(string message)
            => Logger.Log($"[cursor-size] #{debugId} u{state?.Score?.ScoreInfo?.UserID.ToString() ?? "?"} {message}", LoggingTarget.Runtime);

        private void logScale(string trigger)
        {
            string circleSize = state == null
                ? "state=null"
                : $"cs={state.Beatmap.Difficulty.CircleSize:0.##} (factor {GetScaleForCircleSize(state.Beatmap.Difficulty.CircleSize):0.###})";

            string details = $"user={userCursorScale.Value:0.###} auto={autoCursorScale.Value} {circleSize} mod={ModScaleAdjust.Value:0.###} -> scale={cursorScale.Value:0.###}";

            if (details == lastScaleLog || scaleLogCount >= max_scale_logs)
                return;

            lastScaleLog = details;
            scaleLogCount++;

            LogCursorSize($"scale ({trigger}): {details}{(scaleLogCount == max_scale_logs ? " (further scale logs suppressed)" : string.Empty)}");
        }

        private void logSkin(string trigger)
        {
            var component = cursorSprite?.Drawable;
            var expandTarget = (component as SkinnableCursor)?.ExpandTarget;

            // read-only inspection: never look up skin textures here, as lookups mutate the shared Texture.ScaleAdjust.
            string sprites = string.Join(", ", component.ChildrenOfType<Sprite>().Where(s => s is not Box).Select(describeSprite));

            LogCursorSize($"skin ({trigger}): component={component?.GetType().Name ?? "null"} CursorExpand={cursorExpand} "
                          + $"componentSize={formatVector(component?.DrawSize)} expandTarget={expandTarget?.GetType().Name ?? "null"} "
                          + $"size={formatVector(expandTarget?.DrawSize)} scale={formatVector(expandTarget?.Scale)} sprites=[{sprites}]");
        }

        private static string describeSprite(Sprite sprite)
        {
            var texture = sprite.Texture;

            if (texture == null)
                return $"{sprite.GetType().Name}#{RuntimeHelpers.GetHashCode(sprite):x8}(no texture)";

            // a correct sprite has impliedAdj equal on both axes and equal to 1.6 (cursor.png) or 3.2 (cursor@2x.png).
            return $"{sprite.GetType().Name}#{RuntimeHelpers.GetHashCode(sprite):x8}(tex#{RuntimeHelpers.GetHashCode(texture):x8} {texture.Width}x{texture.Height} "
                   + $"adjNow={texture.ScaleAdjust:0.###} size={formatVector(sprite.Size)} impliedAdj={texture.Width / sprite.Size.X:0.###}x{texture.Height / sprite.Size.Y:0.###})";
        }

        /// <summary>
        /// Logs the on-screen size of the cursor body the first time it is measurable, then again once it has settled
        /// at a size more than 1px away from the last logged one, or when it becomes (or stops being) stretched.
        /// </summary>
        private void checkOnScreenSize()
        {
            if (sizeLogCount >= max_size_logs || cursorSprite?.Drawable is not SkinnableCursor component)
                return;

            Drawable body = component.ExpandTarget ?? component;
            var bodyParent = body.Parent;

            if (bodyParent == null || bodyParent.DrawWidth <= 0 || bodyParent.DrawHeight <= 0)
                return;

            // measured through the body's parent so that the body's own Scale/Rotation (expand on click, legacy cursor spin)
            // doesn't count. PopIn/PopOut scale, cursor scale and tile scaling are all included.
            var parentQuad = bodyParent.ScreenSpaceDrawQuad;
            var onScreen = body.DrawSize * new Vector2(parentQuad.Width / bodyParent.DrawWidth, parentQuad.Height / bodyParent.DrawHeight);

            if (onScreen.X <= 0 || onScreen.Y <= 0)
                return;

            double now = debug_stopwatch.Elapsed.TotalMilliseconds;

            if (Math.Abs(onScreen.X - candidateSize.X) > 0.25f || Math.Abs(onScreen.Y - candidateSize.Y) > 0.25f)
            {
                candidateSize = onScreen;
                candidateSince = now;
            }

            var texture = (body as Sprite)?.Texture;
            float naturalAspect = texture != null && texture.Height > 0 ? (float)texture.Width / texture.Height : 1;
            float stretch = onScreen.X / onScreen.Y / naturalAspect;
            bool stretched = Math.Abs(stretch - 1) > 0.02f;

            if (loggedSize is Vector2 last)
            {
                // wait for animations (PopIn/PopOut, scale changes, tile layout) to settle, so this can't log every frame.
                if (now - candidateSince < 250)
                    return;

                if (Math.Abs(onScreen.X - last.X) <= 1 && Math.Abs(onScreen.Y - last.Y) <= 1 && stretched == loggedStretched)
                    return;
            }

            loggedSize = onScreen;
            loggedStretched = stretched;
            sizeLogCount++;

            debugTile ??= (Drawable)this.FindClosestParent<PlayerArea>() ?? this.FindClosestParent<Player>();

            var scaleQuad = cursorScaleContainer.ScreenSpaceDrawQuad;
            string tile = "tile=none";

            if (debugTile != null)
            {
                var tileQuad = debugTile.ScreenSpaceDrawQuad;

                tile = $"tile={debugTile.GetType().Name}{(debugTile is PlayerArea area ? $"(u{area.UserId})" : string.Empty)} "
                       + $"screen={tileQuad.Width:0}x{tileQuad.Height:0} draw={formatVector(debugTile.DrawSize)} rel={onScreen.X / tileQuad.Width:0.0000}";
            }

            LogCursorSize($"on-screen: body={formatVector(onScreen)}px stretch={stretch:0.###}{(stretched ? " STRETCHED" : string.Empty)} "
                          + $"scaleContainerQuad={scaleQuad.Width:0.#}x{scaleQuad.Height:0.#} cursorScale={cursorScale.Value:0.###} "
                          + $"popScale={Scale.X:0.###}x{Scale.Y:0.###} fadeAlpha={Parent?.Alpha:0.##} expandScale={component.ExpandTarget?.Scale.X:0.###} "
                          + $"expands={expandCount}/{contractCount} {tile}"
                          + (sizeLogCount == max_size_logs ? " (further size logs suppressed)" : string.Empty));
        }

        private static string formatVector(Vector2? vector) => vector is Vector2 v ? $"{v.X:0.##}x{v.Y:0.##}" : "null";

        #endregion

        /// <summary>
        /// Get the scale applicable to the ActiveCursor based on a beatmap's circle size.
        /// </summary>
        public static float GetScaleForCircleSize(float circleSize) =>
            1f - 0.7f * (1f + circleSize - BeatmapDifficulty.DEFAULT_DIFFICULTY) / BeatmapDifficulty.DEFAULT_DIFFICULTY;

        private partial class DefaultCursor : SkinnableCursor
        {
            public DefaultCursor()
            {
                RelativeSizeAxes = Axes.Both;

                Anchor = Anchor.Centre;
                Origin = Anchor.Centre;

                InternalChildren = new[]
                {
                    ExpandTarget = new CircularContainer
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        BorderThickness = SIZE / 6,
                        BorderColour = Color4.White,
                        EdgeEffect = new EdgeEffectParameters
                        {
                            Type = EdgeEffectType.Shadow,
                            Colour = Color4.Pink.Opacity(0.5f),
                            Radius = 5,
                        },
                        Children = new Drawable[]
                        {
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Alpha = 0,
                                AlwaysPresent = true,
                            },
                            new CircularContainer
                            {
                                Origin = Anchor.Centre,
                                Anchor = Anchor.Centre,
                                RelativeSizeAxes = Axes.Both,
                                Masking = true,
                                BorderThickness = SIZE / 3,
                                BorderColour = Color4.White.Opacity(0.5f),
                                Children = new Drawable[]
                                {
                                    new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Alpha = 0,
                                        AlwaysPresent = true,
                                    },
                                },
                            },
                        },
                    },
                    new Circle
                    {
                        Origin = Anchor.Centre,
                        Anchor = Anchor.Centre,
                        RelativeSizeAxes = Axes.Both,
                        Scale = new Vector2(0.14f),
                        Colour = new Color4(34, 93, 204, 255),
                        EdgeEffect = new EdgeEffectParameters
                        {
                            Type = EdgeEffectType.Glow,
                            Radius = 8,
                            Colour = Color4.White,
                        },
                    },
                };
            }
        }
    }
}
