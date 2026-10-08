// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Game.Rulesets.Osu.Skinning;

namespace osu.Game.Rulesets.Osu.Tests
{
    [TestFixture]
    public class NonPlayfieldSpriteTest
    {
        /// <summary>
        /// Skin texture stores hand the same <see cref="Framework.Graphics.Textures.Texture"/> instance to every consumer
        /// (e.g. every multiplayer spectator tile). See https://github.com/ppy/osu/issues/14388.
        /// </summary>
        [Test]
        public void TestSharedTextureNotScaledTwice()
        {
            var texture = new DummyRenderer().CreateTexture(152, 152);
            texture.ScaleAdjust = 2;

            var first = new NonPlayfieldSprite { Texture = texture };
            var second = new NonPlayfieldSprite { Texture = texture };

            Assert.That(first.Size.X, Is.EqualTo(47.5f).Within(0.01f));
            Assert.That(second.Size, Is.EqualTo(first.Size));
            Assert.That(texture.ScaleAdjust, Is.EqualTo(2));
        }
    }
}
