using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleSleepSolution
{
    /// <summary>
    /// Client-side HUD showing a sleepiness icon at the bottom-centre of the screen.
    /// Icon is grey when well-rested, yellow at stacks 1-2, orange at 3-4, red at 5-6.
    /// Hover shows a tooltip with current debuff values.
    /// Cloned and adapted from xlib's EffectFrame/EffectBox/EffectTooltip by Xandu —
    /// used with credit under the same open-source spirit.
    /// </summary>
    public class SSSHud : HudElement
    {
        private int currentStacks = 0;
        private int maxStacks = 6;
        private SSSConfig config;

        // Colour bands: green=0, yellow=1-2, orange=3-4, red=5+
        private static readonly double[] ColourGreen = { 0.3, 0.8, 0.3 };
        private static readonly double[] ColourYellow = { 0.95, 0.85, 0.1 };
        private static readonly double[] ColourOrange = { 0.95, 0.5, 0.05 };
        private static readonly double[] ColourRed = { 0.9, 0.15, 0.15 };

        private SSSTooltip tooltip;

        public SSSHud(ICoreClientAPI capi, SSSConfig config) : base(capi)
        {
            this.config = config;
            this.tooltip = new SSSTooltip(capi);
        }

        public void OnStacksChanged(int stacks, int max)
        {
            currentStacks = stacks;
            maxStacks = max;
            Rebuild();
        }

        public void Update()
        {
            // Poll WatchedAttributes as backup in case packet was missed
            int stacks = capi.World.Player?.Entity?.WatchedAttributes.GetInt(SimpleSleepSolutionMod.ATTR_STACKS, 0) ?? 0;
            if (stacks != currentStacks)
            {
                currentStacks = stacks;
                Rebuild();
            }

            // Hide if no stacks and not in warning zone — show grey icon when in warning (stacks=0 but warned)
            if (!IsOpened() && currentStacks > 0) TryOpen();
            if (IsOpened() && currentStacks == 0) TryClose();
        }

        private void Rebuild()
        {
            if (currentStacks == 0)
            {
                TryClose();
                tooltip?.TryClose();
                return;
            }

            double[] colour = GetColour();
            CairoFont iconFont = CairoFont.WhiteMediumText().WithFontSize(28).WithColor(colour);
            CairoFont stackFont = CairoFont.WhiteSmallishText().WithColor(colour);

            // Position at bottom-centre of screen
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterBottom)
                .WithFixedOffset(0, -60);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(6);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            // Icon bounds (moon + zzz text)
            ElementBounds iconBounds = ElementBounds.FixedPos(EnumDialogArea.LeftTop, 4, 4).WithFixedSize(48, 40);
            // Stack count below icon
            ElementBounds countBounds = ElementBounds.FixedPos(EnumDialogArea.LeftTop, 4, 44).WithFixedSize(48, 20);

            bgBounds.WithChildren(iconBounds, countBounds);
            dialogBounds.WithChild(bgBounds);

            string iconText = "☾zzz";
            string stackText = currentStacks + "/" + maxStacks;

            SingleComposer = capi.Gui.CreateCompo("SSSHud", dialogBounds)
                .AddGrayBG(bgBounds)
                .AddStaticText(iconText, iconFont, iconBounds, "sssicon")
                .AddStaticText(stackText, stackFont, countBounds, "ssscount")
                .AddInteractiveElement(new SSSHoverZone(capi, bgBounds, tooltip, this), "ssshover")
                .Compose();

            if (currentStacks > 0) TryOpen();
        }

        private double[] GetColour()
        {
            if (currentStacks <= 0) return ColourGreen;
            if (currentStacks <= 2) return ColourYellow;
            if (currentStacks <= 4) return ColourOrange;
            return ColourRed;
        }

        public string GetTooltipText()
        {
            if (currentStacks == 0) return "";

            double walk = Math.Round(config.WalkPerStack * currentStacks * 100);
            double mining = Math.Round(config.MiningPerStack * currentStacks * 100);
            double melee = Math.Round(config.MeleePerStack * currentStacks * 100);
            double healing = Math.Round(config.HealingPerStack * currentStacks * 100);

            return Lang.Get("simplesleepsolution:sss-tooltip",
                currentStacks, maxStacks,
                walk, mining, melee, healing);
        }

        public override void Dispose()
        {
            base.Dispose();
            tooltip?.Dispose();
        }
    }

    /// <summary>
    /// Invisible interactive element covering the icon that triggers the tooltip on hover.
    /// </summary>
    public class SSSHoverZone : GuiElement
    {
        private SSSTooltip tooltip;
        private SSSHud hud;
        private bool hovering = false;

        public SSSHoverZone(ICoreClientAPI capi, ElementBounds bounds, SSSTooltip tooltip, SSSHud hud)
            : base(capi, bounds)
        {
            this.tooltip = tooltip;
            this.hud = hud;
        }

        public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
        {
            bool inside = IsPositionInside(args.X, args.Y);
            if (inside && !hovering)
            {
                hovering = true;
                tooltip.Show(hud.GetTooltipText(), this.Bounds);
            }
            else if (!inside && hovering)
            {
                hovering = false;
                tooltip.Hide();
            }
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            // Nothing to render — hover zone is invisible
        }
    }

    /// <summary>
    /// Tooltip shown on hover over the sleepiness icon.
    /// Adapted from xlib's EffectTooltip by Xandu.
    /// </summary>
    public class SSSTooltip : HudElement
    {
        private GuiElementDynamicText titleText;
        private GuiElementDynamicText bodyText;

        public SSSTooltip(ICoreClientAPI capi) : base(capi)
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterBottom)
                .WithFixedOffset(70, -60);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds titleBounds = ElementBounds.Fixed(0, 0, 220, 24);
            ElementBounds bodyBounds = ElementBounds.Fixed(0, 28, 220, 80);

            bgBounds.WithChildren(titleBounds, bodyBounds);
            dialogBounds.WithChild(bgBounds);

            SingleComposer = capi.Gui.CreateCompo("SSSTooltip", dialogBounds)
                .AddDialogBG(bgBounds, false)
                .AddDynamicText("", CairoFont.WhiteSmallishText(), titleBounds, "sssttitle")
                .AddDynamicText("", CairoFont.WhiteDetailText(), bodyBounds, "sssttbody")
                .Compose();

            titleText = SingleComposer.GetDynamicText("sssttitle");
            bodyText = SingleComposer.GetDynamicText("sssttbody");
        }

        public void Show(string text, ElementBounds near)
        {
            if (string.IsNullOrEmpty(text)) return;
            titleText.SetNewText(Lang.Get("simplesleepsolution:sss-tooltip-title"));
            bodyText.SetNewText(text);
            SingleComposer.ReCompose();
            TryOpen();
        }

        public void Hide()
        {
            TryClose();
        }
    }
}