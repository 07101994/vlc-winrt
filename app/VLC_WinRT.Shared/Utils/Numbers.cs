﻿using VLC_WinRT.Helpers;

namespace VLC_WinRT.Utils
{
    /// <summary>
    /// Magic numbers belong here
    /// </summary>
    public static class Numbers
    {
        // Under this number, list should not display SemanticZoom (letters or anything), only a flat list
        public static readonly int SemanticZoomItemCountThreshold = 25;

        // Database Version 
        public static readonly int DbVersion = 7;
        public static bool NeedsToDrop()
        {
            if (!ApplicationSettingsHelper.Contains(Strings.AlreadyLaunched) || ApplicationSettingsHelper.Contains(Strings.DatabaseVersion) && (int)ApplicationSettingsHelper.ReadSettingsValue(Strings.DatabaseVersion) == Numbers.DbVersion)
            {
                LogHelper.Log("DB does not need to be dropped.");
                return false;
            }
            else
            {
                LogHelper.Log("DB needs to be dropped.");
                ApplicationSettingsHelper.SaveSettingsValue(Strings.DatabaseVersion, Numbers.DbVersion);
                ApplicationSettingsHelper.SaveSettingsValue(Strings.AlreadyLaunched, true);
                return true;
            }
        }
    }
}
