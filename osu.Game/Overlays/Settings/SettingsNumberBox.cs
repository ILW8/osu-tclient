﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;

namespace osu.Game.Overlays.Settings
{
    internal partial class OutlinedNumberBox : OutlinedTextBox
    {
        [Obsolete("Use InputProperties instead.")]
        protected override bool AllowIme => false;

        protected override bool CanAddCharacter(char character) => char.IsAsciiDigit(character);

        public new void NotifyInputError() => base.NotifyInputError();
    }

    public partial class SettingsLongNumberBox : SettingsItem<long?>
    {
        protected override Drawable CreateControl() => new NumberControl
        {
            RelativeSizeAxes = Axes.X,
        };

        private sealed partial class NumberControl : CompositeDrawable, IHasCurrentValue<long?>
        {
            private readonly BindableWithCurrent<long?> current = new BindableWithCurrent<long?>();

            public Bindable<long?> Current
            {
                get => current.Current;
                set => current.Current = value;
            }

            public NumberControl()
            {
                AutoSizeAxes = Axes.Y;

                OutlinedNumberBox numberBox;

                InternalChildren = new[]
                {
                    numberBox = new OutlinedNumberBox
                    {
                        RelativeSizeAxes = Axes.X,
                        CommitOnFocusLost = true
                    }
                };

                numberBox.Current.BindValueChanged(e =>
                {
                    if (string.IsNullOrEmpty(e.NewValue))
                    {
                        Current.Value = null;
                        return;
                    }

                    if (long.TryParse(e.NewValue, out long intVal))
                        Current.Value = intVal;
                    else
                        numberBox.NotifyInputError();

                    // trigger Current again to either restore the previous text box value, or to reformat the new value via .ToString().
                    Current.TriggerChange();
                });

                Current.BindValueChanged(e =>
                {
                    numberBox.Current.Value = e.NewValue?.ToString();
                });
            }
        }
    }

    public partial class SettingsNumberBox : SettingsItem<int?>
    {
        protected override Drawable CreateControl() => new NumberControl
        {
            RelativeSizeAxes = Axes.X,
        };

        private sealed partial class NumberControl : CompositeDrawable, IHasCurrentValue<int?>
        {
            private readonly BindableWithCurrent<int?> current = new BindableWithCurrent<int?>();

            public Bindable<int?> Current
            {
                get => current.Current;
                set => current.Current = value;
            }

            public NumberControl()
            {
                AutoSizeAxes = Axes.Y;

                OutlinedNumberBox numberBox;

                InternalChildren = new[]
                {
                    numberBox = new OutlinedNumberBox
                    {
                        RelativeSizeAxes = Axes.X,
                        CommitOnFocusLost = true
                    }
                };

                numberBox.Current.BindValueChanged(e =>
                {
                    if (string.IsNullOrEmpty(e.NewValue))
                    {
                        Current.Value = null;
                        return;
                    }

                    if (int.TryParse(e.NewValue, out int intVal))
                        Current.Value = intVal;
                    else
                        numberBox.NotifyInputError();

                    // trigger Current again to either restore the previous text box value, or to reformat the new value via .ToString().
                    Current.TriggerChange();
                });

                Current.BindValueChanged(e =>
                {
                    numberBox.Current.Value = e.NewValue?.ToString();
                });
            }
        }
    }
}
