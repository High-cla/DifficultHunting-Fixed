using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml.Linq;
using System.IO;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;



namespace AdvancedAutopilot
{
  public partial class Mod : IAssemblyPlugin
  {
    public static Harmony harmony;

    public static float AutoPilotSteeringLerp = 0.2f;
    public static float AutopilotMinDistToPathNode = 3000.0f;
    public static float AutopilotRayCastInterval = 0.25f; // default of 0.5f

    public static float AutoPilotMaxSpeed = 1.0f; // default of 0.5f

    public static float AIPilotMaxSpeed = 10.0f; // default of 1.0f

    public static float RecalculatePathInterval = 2.5f; // default of 5.0f

    public static bool AggressiveAutopilotEnabled = true;

    public void Initialize()
    {
      harmony = new Harmony("advanced.autopilot");

      LoadConfig();

      PatchOnBothSides();
      
#if CLIENT
      PatchClientSide();
#endif
    }

    private void LoadConfig()
    {
      try
      {
        // Use LocalAppData for config storage
        string configDir = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "Daedalic Entertainment GmbH",
          "Barotrauma",
          "ModConfigs"
        );
        
        string configPath = Path.Combine(configDir, "AdvancedAutopilot.xml");
        
        // Ensure directory exists
        if (!Directory.Exists(configDir))
        {
          Directory.CreateDirectory(configDir);
          Log($"[AdvancedAutopilot] Created config directory: {configDir}", Color.Cyan);
        }
        
        // If config doesn't exist, create it with default values
        if (!File.Exists(configPath))
        {
          XDocument defaultConfig = new XDocument(
            new XElement("ModConfig",
              new XElement("AggressiveAutopilot", "True")
            )
          );
          
          defaultConfig.Save(configPath);
          Log($"[AdvancedAutopilot] Created default config at {configPath}", Color.Green);
        }
        
        // Load the config
        XDocument doc = XDocument.Load(configPath);
        XElement aggressiveElement = doc.Root?.Element("AggressiveAutopilot");
        
        if (aggressiveElement != null)
        {
          string value = aggressiveElement.Value.Trim();
          AggressiveAutopilotEnabled = bool.Parse(value);
          Log($"[AdvancedAutopilot] Config loaded: AggressiveAutopilot = {AggressiveAutopilotEnabled}", Color.Green);
        }
        else
        {
          Log("[AdvancedAutopilot] AggressiveAutopilot element not found in config, using default value: True", Color.Yellow);
        }
      }
      catch (Exception ex)
      {
        Log($"[AdvancedAutopilot] Error loading config: {ex.Message}, using default value: True", Color.Red);
      }
    }    
    
    public void PatchOnBothSides()
    {
      if (AggressiveAutopilotEnabled)
      {        
        PatchSharedGetSteeringVelocity();
        PatchSharedTargetVelocity();
        PatchSharedUpdateAutoPilot();
        PatchSharedUpdate();
        Log("[AdvancedAutopilot] Aggressive Autopilot is ENABLED! This is the default experience for the mod. However, if you would like to disable it, please edit the config file located in LocalAppData\\Daedalic Entertainment GmbH\\Barotrauma\\ModConfigs\\AdvancedAutopilot.xml", Color.Lime);
      } 
      else
      {
        Log("[AdvancedAutopilot] Aggressive Autopilot is DISABLED! If you would like to re-enable it, please edit the config file located in LocalAppData\\Daedalic Entertainment GmbH\\Barotrauma\\ModConfigs\\AdvancedAutopilot.xml", Color.Yellow);
      }
      
      PatchSharedUpdatePath();
      PatchSharedFindPath();
    }

#if CLIENT
    public void PatchClientSide()
    {
      PatchClientCreateGUI();
    }
#endif


    public static void Log(object msg, Color? cl = null)
    {
      cl ??= Color.Cyan;
      LuaCsLogger.LogMessage($"{msg ?? "null"}", cl * 0.8f, cl);
    }

    public void OnLoadCompleted() { }
    public void PreInitPatching() { }
    public void Dispose()
    {
      harmony.UnpatchSelf();
    }
  }
}