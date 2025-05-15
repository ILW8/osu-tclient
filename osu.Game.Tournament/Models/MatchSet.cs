// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;

namespace osu.Game.Tournament.Models
{
    public class MatchSet
    {
        public BindableLong Map1Id = new BindableLong();
        public BindableLong Map2Id = new BindableLong();
    }
}
