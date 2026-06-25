using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.Xna.Framework;

using FarseerPhysics;
using Voronoi2;

using HarmonyLib;

using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;


namespace AdvancedAutopilot
{
  public partial class Mod : IAssemblyPlugin
  {
    public static void PatchClientCreateGUI()
    {
      harmony.Patch(
        original: typeof(Steering).GetMethod("CreateGUI", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Steering_CreateGUI_Replace"))
      );
    }

    //https://github.com/evilfactory/LuaCsForBarotrauma/blob/9799a2a97bd5a925de774f2bce26c02154fb2c69/Barotrauma/BarotraumaClient/ClientSource/Items/Components/Machines/Steering.cs#L149
    public static bool Steering_CreateGUI_Replace(Steering __instance)
    {
      Steering _ = __instance;

      // Main control container
      _.ControlContainer = new GUIFrame(
        new RectTransform(new Vector2(Sonar.controlBoxSize.X, 1 - Sonar.controlBoxSize.Y * 2), _.GuiFrame.RectTransform, Anchor.CenterRight),
        "ItemUI");

      var paddedControlContainer = new GUIFrame(
        new RectTransform(_.ControlContainer.Rect.Size - GUIStyle.ItemFrameMargin, _.ControlContainer.RectTransform, Anchor.Center)
        {
          AbsoluteOffset = GUIStyle.ItemFrameOffset
        },
        style: null);

      // Steering mode area (switch + indicators)
      var steeringModeArea = new GUIFrame(new RectTransform(new Vector2(1, 0.4f), paddedControlContainer.RectTransform, Anchor.TopLeft), style: null);

      _.steeringModeSwitch = new GUIButton(new RectTransform(new Vector2(0.2f, 1), steeringModeArea.RectTransform), string.Empty, style: "SwitchVertical")
      {
        UserData = UIHighlightAction.ElementId.SteeringModeSwitch,
        Selected = _.autoPilot,
        Enabled = true,
        ClickSound = GUISoundType.UISwitch,
        OnClicked = (button, data) =>
        {
          button.Selected = !button.Selected;
          _.AutoPilot = button.Selected;
          if (GameMain.Client != null)
          {
            _.unsentChanges = true;
            _.user = Character.Controlled;
          }
          return true;
        }
      };

      var steeringModeRightSide = new GUIFrame(
        new RectTransform(new Vector2(1.0f - _.steeringModeSwitch.RectTransform.RelativeSize.X, 0.8f), steeringModeArea.RectTransform, Anchor.CenterLeft)
        {
          RelativeOffset = new Vector2(_.steeringModeSwitch.RectTransform.RelativeSize.X, 0)
        },
        style: null);

      _.manualPilotIndicator = new GUITickBox(
        new RectTransform(new Vector2(1, 0.45f), steeringModeRightSide.RectTransform, Anchor.TopLeft),
        TextManager.Get("SteeringManual"),
        font: GUIStyle.SubHeadingFont,
        style: "IndicatorLightRedSmall")
      {
        Selected = !_.autoPilot,
        Enabled = false
      };

      _.autopilotIndicator = new GUITickBox(
        new RectTransform(new Vector2(1, 0.45f), steeringModeRightSide.RectTransform, Anchor.BottomLeft),
        TextManager.Get("SteeringAutoPilot"),
        font: GUIStyle.SubHeadingFont,
        style: "IndicatorLightRedSmall")
      {
        Selected = _.autoPilot,
        Enabled = false
      };

      _.manualPilotIndicator.TextBlock.OverrideTextColor(GUIStyle.TextColorNormal);
      _.autopilotIndicator.TextBlock.OverrideTextColor(GUIStyle.TextColorNormal);
      GUITextBlock.AutoScaleAndNormalize(_.manualPilotIndicator.TextBlock, _.autopilotIndicator.TextBlock);

      // Autopilot controls (maintain pos / level start / level end)
      var autoPilotControls = new GUIFrame(new RectTransform(new Vector2(0.75f, 0.62f), paddedControlContainer.RectTransform, Anchor.BottomCenter), "OutlineFrame");
      var paddedAutoPilotControls = new GUIFrame(new RectTransform(new Vector2(0.92f, 0.88f), autoPilotControls.RectTransform, Anchor.Center), style: null);

      int textLimit = (int)(paddedAutoPilotControls.Rect.Width * 0.75f);

      _.maintainPosTickBox = new GUITickBox(
        new RectTransform(new Vector2(1, 0.333f), paddedAutoPilotControls.RectTransform, Anchor.TopCenter),
        ToolBox.LimitString(TextManager.Get("SteeringMaintainPos"), GUIStyle.SmallFont, textLimit),
        font: GUIStyle.SmallFont,
        style: "GUIRadioButton")
      {
        UserData = UIHighlightAction.ElementId.MaintainPosTickBox,
        Enabled = _.autoPilot,
        Selected = _.maintainPos,
        OnSelected = tickBox =>
        {
          if (_.maintainPos != tickBox.Selected)
          {
            _.unsentChanges = true;
            _.user = Character.Controlled;
            _.maintainPos = tickBox.Selected;
            if (_.maintainPos)
            {
              if (_.controlledSub == null)
              {
                _.posToMaintain = null;
              }
              else
              {
                _.posToMaintain = _.controlledSub.WorldPosition;
              }
            }
            else if (!_.LevelEndSelected && !_.LevelStartSelected)
            {
              _.AutoPilot = false;
            }
            if (!_.maintainPos)
            {
              _.posToMaintain = null;
            }
          }
          return true;
        }
      };

      _.levelStartTickBox = new GUITickBox(
        new RectTransform(new Vector2(1, 0.333f), paddedAutoPilotControls.RectTransform, Anchor.Center),
        GameMain.GameSession?.StartLocation == null ? "" : ToolBox.LimitString("Pilot to Objective(s)", GUIStyle.SmallFont, textLimit),
        font: GUIStyle.SmallFont,
        style: "GUIRadioButton")
      {
        Enabled = _.autoPilot,
        Selected = _.levelStartSelected,
        OnSelected = tickBox =>
        {
          if (_.levelStartSelected != tickBox.Selected)
          {
            _.unsentChanges = true;
            _.user = Character.Controlled;
            _.levelStartSelected = tickBox.Selected;
            _.levelEndSelected = !_.levelStartSelected;
            if (_.levelStartSelected)
            {
              _.UpdatePath();
            }
            else if (!_.MaintainPos && !_.LevelEndSelected)
            {
              _.AutoPilot = false;
            }
          }
          return true;
        }
      };

      _.levelEndTickBox = new GUITickBox(
        new RectTransform(new Vector2(1, 0.333f), paddedAutoPilotControls.RectTransform, Anchor.BottomCenter),
        (GameMain.GameSession?.EndLocation == null || Level.IsLoadedOutpost) ? "" : ToolBox.LimitString(GameMain.GameSession.EndLocation.DisplayName, GUIStyle.SmallFont, textLimit),
        font: GUIStyle.SmallFont,
        style: "GUIRadioButton")
      {
        Enabled = _.autoPilot,
        Selected = _.levelEndSelected,
        Visible = GameMain.GameSession?.EndLocation != null,
        OnSelected = tickBox =>
        {
          if (_.levelEndSelected != tickBox.Selected)
          {
            _.unsentChanges = true;
            _.user = Character.Controlled;
            _.levelEndSelected = tickBox.Selected;
            _.levelStartSelected = !_.levelEndSelected;
            if (_.levelEndSelected)
            {
              _.UpdatePath();
            }
            else if (!_.MaintainPos && !_.LevelStartSelected)
            {
              _.AutoPilot = false;
            }
          }
          return true;
        }
      };

      _.maintainPosTickBox.RectTransform.IsFixedSize = _.levelStartTickBox.RectTransform.IsFixedSize = _.levelEndTickBox.RectTransform.IsFixedSize = false;
      _.maintainPosTickBox.RectTransform.MaxSize = _.levelStartTickBox.RectTransform.MaxSize = _.levelEndTickBox.RectTransform.MaxSize =
        new Point(int.MaxValue, paddedAutoPilotControls.Rect.Height / 3);
      _.maintainPosTickBox.RectTransform.MinSize = _.levelStartTickBox.RectTransform.MinSize = _.levelEndTickBox.RectTransform.MinSize = Point.Zero;

      GUITextBlock.AutoScaleAndNormalize(scaleHorizontal: false, scaleVertical: true, _.maintainPosTickBox.TextBlock, _.levelStartTickBox.TextBlock, _.levelEndTickBox.TextBlock);

      GUIRadioButtonGroup destinations = new GUIRadioButtonGroup();
      destinations.AddRadioButton((int)Steering.Destination.MaintainPos, _.maintainPosTickBox);
      destinations.AddRadioButton((int)Steering.Destination.LevelStart, _.levelStartTickBox);
      destinations.AddRadioButton((int)Steering.Destination.LevelEnd, _.levelEndTickBox);
      destinations.Selected = (int)(_.maintainPos ? Steering.Destination.MaintainPos :
                    _.levelStartSelected ? Steering.Destination.LevelStart : Steering.Destination.LevelEnd);

      // Status
      _.statusContainer = new GUIFrame(
        new RectTransform(Sonar.controlBoxSize, _.GuiFrame.RectTransform, Anchor.BottomRight)
        {
          RelativeOffset = Sonar.controlBoxOffset
        },
        "ItemUI");

      var paddedStatusContainer = new GUIFrame(
        new RectTransform(_.statusContainer.Rect.Size - GUIStyle.ItemFrameMargin, _.statusContainer.RectTransform, Anchor.Center, isFixedSize: false)
        {
          AbsoluteOffset = GUIStyle.ItemFrameOffset
        },
        style: null);

      var elements = GUI.CreateElements(3, new Vector2(1f, 0.333f), paddedStatusContainer.RectTransform, rt => new GUIFrame(rt, style: null), Anchor.TopCenter, relativeSpacing: 0.01f);

      List<GUIComponent> leftElements = new List<GUIComponent>(), centerElements = new List<GUIComponent>(), rightElements = new List<GUIComponent>();
      for (int i = 0; i < elements.Count; i++)
      {
        var e = elements[i];
        var group = new GUILayoutGroup(new RectTransform(Vector2.One, e.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
        {
          RelativeSpacing = 0.01f,
          Stretch = true
        };

        var left = new GUIFrame(new RectTransform(new Vector2(0.45f, 1), group.RectTransform), style: null);
        var center = new GUIFrame(new RectTransform(new Vector2(0.15f, 1), group.RectTransform), style: null);
        var right = new GUIFrame(new RectTransform(new Vector2(0.4f, 0.8f), group.RectTransform), style: null);

        leftElements.Add(left);
        centerElements.Add(center);
        rightElements.Add(right);

        LocalizedString leftText = string.Empty, centerText = string.Empty;
        GUITextBlock.TextGetterHandler rightTextGetter = null;

        switch (i)
        {
          case 0:
            leftText = TextManager.Get("DescentVelocity");
            centerText = TextManager.Get("KilometersPerHour");
            rightTextGetter = () =>
            {
              Vector2 vel = _.controlledSub == null ? Vector2.Zero : _.controlledSub.Velocity;
              var realWorldVel = ConvertUnits.ToDisplayUnits(vel.Y * Physics.DisplayToRealWorldRatio) * 3.6f;
              return (-realWorldVel).ToString("0.0");
            };
            break;

          case 1:
            leftText = TextManager.Get("Velocity");
            centerText = TextManager.Get("KilometersPerHour");
            rightTextGetter = () =>
            {
              Vector2 vel = _.controlledSub == null ? Vector2.Zero : _.controlledSub.Velocity;
              var realWorldVel = ConvertUnits.ToDisplayUnits(vel.X * Physics.DisplayToRealWorldRatio) * 3.6f;
              if (_.controlledSub != null && _.controlledSub.FlippedX) { realWorldVel *= -1; }
              return realWorldVel.ToString("0.0");
            };
            break;

          case 2:
            leftText = TextManager.Get("Depth");
            centerText = TextManager.Get("Meter");
            rightTextGetter = () =>
            {
              if (Level.Loaded is { IsEndBiome: true })
              {
                return Timing.TotalTime % 5.0f < 0.5f ? Rand.Range(-9000, 9000).ToString() : "ERROR";
              }
              float realWorldDepth = _.controlledSub == null ? -1000.0f : _.controlledSub.RealWorldDepth;
              return ((int)realWorldDepth).ToString();
            };
            break;
        }

        new GUITextBlock(new RectTransform(Vector2.One, left.RectTransform), leftText, font: GUIStyle.SubHeadingFont, wrap: leftText.Contains(" "), textAlignment: Alignment.CenterRight);
        new GUITextBlock(new RectTransform(Vector2.One, center.RectTransform), centerText, font: GUIStyle.Font, textAlignment: Alignment.Center) { Padding = Vector4.Zero };

        var digitalFrame = new GUIFrame(new RectTransform(Vector2.One, right.RectTransform), style: "DigitalFrameDark");
        new GUITextBlock(new RectTransform(Vector2.One * 0.85f, digitalFrame.RectTransform, Anchor.Center), "12345", GUIStyle.TextColorDark, GUIStyle.DigitalFont, Alignment.CenterRight)
        {
          TextGetter = rightTextGetter
        };
      }

      GUITextBlock.AutoScaleAndNormalize(leftElements.SelectMany(e => e.GetAllChildren<GUITextBlock>()));
      GUITextBlock.AutoScaleAndNormalize(centerElements.SelectMany(e => e.GetAllChildren<GUITextBlock>()));
      GUITextBlock.AutoScaleAndNormalize(rightElements.SelectMany(e => e.GetAllChildren<GUITextBlock>()));

      // Docking interface ----------------------------------------------------
      float dockingButtonSize = 1.1f;
      float elementScale = 0.6f;

      _.dockingContainer = new GUIFrame(new RectTransform(Sonar.controlBoxSize, _.GuiFrame.RectTransform, Anchor.BottomRight, scaleBasis: ScaleBasis.Smallest)
      {
        RelativeOffset = new Vector2(Sonar.controlBoxOffset.X + 0.05f, -0.05f)
      }, style: null);

      _.dockText = TextManager.Get("label.navterminaldock", "captain.dock");
      _.undockText = TextManager.Get("label.navterminalundock", "captain.undock");

      _.dockingButton = new GUIButton(new RectTransform(new Vector2(elementScale), _.dockingContainer.RectTransform, Anchor.Center), _.dockText, style: "PowerButton")
      {
        OnClicked = (btn, userdata) =>
        {
          if (GameMain.GameSession?.Missions.Any(m => !m.AllowUndocking) ?? false)
          {
            new GUIMessageBox("", TextManager.Get("undockingdisabledbymission"));
            return false;
          }

          if (GameMain.GameSession?.Campaign is CampaignMode campaign)
          {
            if (Level.IsLoadedOutpost && _.DockingSources.Any(d => d.Docked && (d.DockingTarget?.Item.Submarine?.Info?.IsOutpost ?? false)))
            {
              // Undocking from an outpost
              if (!ObjectiveManager.AllActiveObjectivesCompleted())
              {
                _.exitOutpostPrompt = new GUIMessageBox(
                  "",
                  TextManager.GetWithVariable("CampaignExitTutorialOutpostPrompt", "[locationname]", campaign.Map.CurrentLocation.DisplayName),
                  new LocalizedString[] { TextManager.Get("yes"), TextManager.Get("no") });

                _.exitOutpostPrompt.Buttons[0].OnClicked += (_, _) =>
                {
                  _.exitOutpostPrompt.Close();
                  return OpenMap(campaign);
                };

                _.exitOutpostPrompt.Buttons[1].OnClicked += _.exitOutpostPrompt.Close;
                return false;
              }
              return OpenMap(campaign);
            }

            if (!Level.IsLoadedOutpost && _.DockingModeEnabled && _.ActiveDockingSource != null &&
                !_.ActiveDockingSource.Docked && _.DockingTarget?.Item?.Submarine == Level.Loaded.StartOutpost && (_.DockingTarget?.Item?.Submarine?.Info.IsOutpost ?? false))
            {
              // Docking to an outpost
              var subsToLeaveBehind = CampaignMode.GetSubsToLeaveBehind(_.Item.Submarine);
              if (subsToLeaveBehind.Any())
              {
                _.enterOutpostPrompt = new GUIMessageBox(
                  TextManager.GetWithVariable("enterlocation", "[locationname]", _.DockingTarget.Item.Submarine.Info.Name),
                  TextManager.Get(subsToLeaveBehind.Count == 1 ? "LeaveSubBehind" : "LeaveSubsBehind"),
                  new LocalizedString[] { TextManager.Get("yes"), TextManager.Get("no") });
              }
              else
              {
                _.enterOutpostPrompt = new GUIMessageBox(
                  "",
                  TextManager.GetWithVariable("campaignenteroutpostprompt", "[locationname]", _.DockingTarget.Item.Submarine.Info.Name),
                  new LocalizedString[] { TextManager.Get("yes"), TextManager.Get("no") });
              }

              _.enterOutpostPrompt.Buttons[0].OnClicked += (btn, userdata) =>
              {
                SendDockingSignal();
                _.enterOutpostPrompt.Close();
                return true;
              };

              _.enterOutpostPrompt.Buttons[1].OnClicked += _.enterOutpostPrompt.Close;
              return false;
            }
          }

          SendDockingSignal();
          return true;
        }
      };

      bool OpenMap(CampaignMode campaign)
      {
        campaign.ShowCampaignUI = true;
        campaign.CampaignUI.SelectTab(CampaignMode.InteractionType.Map);
        return false;
      }

      void SendDockingSignal()
      {
        if (GameMain.Client == null)
        {
          _.item.SendSignal(new Signal("1", sender: Character.Controlled), "toggle_docking");
        }
        else
        {
          _.dockingNetworkMessagePending = true;
          _.item.CreateClientEvent(_);
        }
      }

      _.dockingButton.Font = GUIStyle.SubHeadingFont;
      _.dockingButton.TextBlock.RectTransform.MaxSize = new Point((int)(_.dockingButton.Rect.Width * 0.7f), int.MaxValue);
      _.dockingButton.TextBlock.AutoScaleHorizontal = true;

      var style = GUIStyle.GetComponentStyle("DockingButtonUp");
      Sprite buttonSprite = style.Sprites.FirstOrDefault().Value.FirstOrDefault()?.Sprite;
      Point buttonSize = buttonSprite != null ? buttonSprite.size.ToPoint() : new Point(149, 52);
      Point horizontalButtonSize = buttonSize.Multiply(elementScale * GUI.Scale * dockingButtonSize);
      Point verticalButtonSize = horizontalButtonSize.Flip();

      var leftButton = new GUIButton(new RectTransform(verticalButtonSize, _.dockingContainer.RectTransform, Anchor.CenterLeft), "", style: "DockingButtonLeft")
      {
        OnClicked = _.NudgeButtonClicked,
        UserData = -Vector2.UnitX
      };

      var rightButton = new GUIButton(new RectTransform(verticalButtonSize, _.dockingContainer.RectTransform, Anchor.CenterRight), "", style: "DockingButtonRight")
      {
        OnClicked = _.NudgeButtonClicked,
        UserData = Vector2.UnitX
      };

      var upButton = new GUIButton(new RectTransform(horizontalButtonSize, _.dockingContainer.RectTransform, Anchor.TopCenter), "", style: "DockingButtonUp")
      {
        OnClicked = _.NudgeButtonClicked,
        UserData = Vector2.UnitY
      };

      var downButton = new GUIButton(new RectTransform(horizontalButtonSize, _.dockingContainer.RectTransform, Anchor.BottomCenter), "", style: "DockingButtonDown")
      {
        OnClicked = _.NudgeButtonClicked,
        UserData = -Vector2.UnitY
      };

      // Sonar area
      _.steerArea = new GUICustomComponent(
        new RectTransform(Sonar.GUISizeCalculation, _.GuiFrame.RectTransform, Anchor.CenterLeft, scaleBasis: ScaleBasis.Smallest),
        (spriteBatch, guiCustomComponent) => { _.DrawHUD(spriteBatch, guiCustomComponent.Rect); },
        null);

      _.steerRadius = _.steerArea.Rect.Width / 2;

      _.iceSpireWarningText = new GUITextBlock(
        new RectTransform(new Vector2(0.5f, 0.25f), _.steerArea.RectTransform, Anchor.Center, Pivot.TopCenter),
        TextManager.Get("NavTerminalIceSpireWarning"),
        GUIStyle.Red,
        GUIStyle.SubHeadingFont,
        Alignment.Center,
        color: Color.Black * 0.8f,
        wrap: true)
      {
        Visible = false
      };

      _.pressureWarningText = new GUITextBlock(
        new RectTransform(new Vector2(0.5f, 0.25f), _.steerArea.RectTransform, Anchor.Center, Pivot.TopCenter),
        TextManager.Get("SteeringDepthWarning"),
        GUIStyle.Red,
        GUIStyle.SubHeadingFont,
        Alignment.Center,
        color: Color.Black * 0.8f)
      {
        Visible = false
      };

      // Tooltip/helper text
      _.tipContainer = new GUITextBlock(
        new RectTransform(new Vector2(0.5f, 0.1f), _.steerArea.RectTransform, Anchor.BottomCenter, Pivot.TopCenter),
        "",
        font: GUIStyle.Font,
        wrap: true,
        style: "GUIToolTip",
        textAlignment: Alignment.Center)
      {
        AutoScaleHorizontal = true
      };

      _.noPowerTip = TextManager.Get("SteeringNoPowerTip");
      _.autoPilotMaintainPosTip = TextManager.Get("SteeringAutoPilotMaintainPosTip");
      _.autoPilotLevelStartTip = TextManager.GetWithVariable("SteeringAutoPilotLocationTip", "[locationname]",
        GameMain.GameSession?.StartLocation == null ? "Start" : GameMain.GameSession.StartLocation.DisplayName);
      _.autoPilotLevelEndTip = TextManager.GetWithVariable("SteeringAutoPilotLocationTip", "[locationname]",
        GameMain.GameSession?.EndLocation == null ? "End" : GameMain.GameSession.EndLocation.DisplayName);

      return false; // skip original method
    }
  }
}
