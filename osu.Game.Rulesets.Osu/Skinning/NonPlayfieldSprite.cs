// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Osu.Skinning
{
    /// <summary>
    /// A sprite which is displayed within the playfield, but historically was not considered part of the playfield.
    /// Performs scale adjustment to undo the scale applied by <see cref="PlayfieldAdjustmentContainer"/> (osu! ruleset specifically).
    /// </summary>
    public partial class NonPlayfieldSprite : Sprite
    {
        public override Texture? Texture
        {
            get => base.Texture;
            set
            {
                // [cursor-size] diagnostics. the texture instance is shared by every user of the skin, so ScaleAdjust is shared mutable state.
                float scaleAdjustIn = value?.ScaleAdjust ?? 0;

                if (value != null)
                    // stable "magic ratio". see OsuPlayfieldAdjustmentContainer for full explanation.
                    value.ScaleAdjust *= 1.6f;
                base.Texture = value;

                if (value != null)
                {
                    // expected: adjIn is 1 (or 2 for @2x) and impliedAdj is exactly adjIn * 1.6 on both axes.
                    Logger.Log($"[cursor-size] {nameof(NonPlayfieldSprite)}#{RuntimeHelpers.GetHashCode(this):x8} "
                               + $"tex#{RuntimeHelpers.GetHashCode(value):x8} {value.Width}x{value.Height} "
                               + $"adjIn={scaleAdjustIn:0.###} size={Size.X:0.##}x{Size.Y:0.##} impliedAdj={value.Width / Size.X:0.###}x{value.Height / Size.Y:0.###} "
                               + $"adjNow={value.ScaleAdjust:0.###} thread={Environment.CurrentManagedThreadId}", LoggingTarget.Runtime);
                }
            }
        }
    }
}
