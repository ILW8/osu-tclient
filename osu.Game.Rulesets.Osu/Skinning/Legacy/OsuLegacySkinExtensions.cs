// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;

namespace osu.Game.Rulesets.Osu.Skinning.Legacy
{
    public static class OsuLegacySkinExtensions
    {
        /// <summary>
        /// Returns a copy of <paramref name="texture"/> with the stable "magic ratio" applied (see <see cref="UI.OsuPlayfieldAdjustmentContainer"/> for full explanation).
        /// </summary>
        /// <remarks>
        /// Skin texture lookups return the same instance to every consumer, so the shared <see cref="Texture.ScaleAdjust"/> must not be modified in place.
        /// Concurrent loads (e.g. multiplayer spectator tiles) would otherwise compound the ratio. See https://github.com/ppy/osu/issues/14388.
        /// </remarks>
        [return: NotNullIfNotNull(nameof(texture))]
        public static Texture? WithMagicRatio(this Texture? texture)
        {
            if (texture == null)
                return null;

            var adjusted = texture.Crop(new RectangleF(0, 0, texture.Width, texture.Height));
            adjusted.ScaleAdjust = texture.ScaleAdjust * 1.6f;
            return adjusted;
        }
    }
}
