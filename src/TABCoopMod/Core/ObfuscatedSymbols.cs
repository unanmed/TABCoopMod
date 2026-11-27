namespace TABCoopMod.Core
{
    /// <summary>
    /// Centralized dictionary of Eazfuscator.NET obfuscated type, method, and field names
    /// in TheyAreBillions.exe (v1.0.14.29 x64).
    /// NEVER hardcode #=z... strings outside this class and GameBridge!
    /// See docs/03_REVERSE_ENGINEERING_MAP.md for full documentation.
    /// </summary>
    public static class ObfuscatedSymbols
    {
        // ====================================================================
        // 1. Top-Level Core Systems (Global Namespace)
        // ====================================================================
        public const string Type_ZXGame             = "#=z4RevDP3eECXqXS6JRA==";
        public const string Type_ZXSystem           = "#=zsW2J3r72Cu83";
        public const string Type_ZXSystemGameLevel  = "#=zxRcpu6e7NYzT7tGWqPjpOkc=";
        public const string Type_ZXSystemMainMenu   = "#=zLs$VX1eoAapVflmW_w==";
        public const string Type_ZXSystemCampaign   = "#=zUsJtyOzSfYDnqdeHjCSzszBbekLj";
        public const string Type_ZXSystemMapLayers  = "#=zJme8KFhmikprnkg_2CDeiQE=";
        /// <summary>Static singleton field holding the live ZXSystemMapLayers (fog config) instance.</summary>
        public const string Field_MapLayers_Current  = "#=zQFWYIvs=";
        /// <summary>Foreground (current) watch-visibility array used by CInvisibleWhenNotWatched.</summary>
        public const string Field_MapLayers_VisibleCells   = "#=z69ntQguW1qCr";
        /// <summary>Back buffer swapped into VisibleCells every 20 logic ticks.</summary>
        public const string Field_MapLayers_VisibleBackBuf = "#=z7sWPJNagVkip";
        /// <summary>Explored/fog array used by the explored-area visibility overload.</summary>
        public const string Field_MapLayers_ExploredCells  = "#=zwJQaMvq$T3G4";
        public const string Type_ZXSystemAudio      = "#=zJYbPa4gP2VI16zEh5y2Zxew=";
        public const string Type_ZXSystemBuilder    = "#=z5Ns_eH8ENLF7HFU5mg==";
        public const string Type_ZXSteamManager     = "#=z3Zxcp6RwVCZHa9xpeg==";
        public const string Type_ZXMessageBoxHelper = "#=zzi2cl4h1HYA5cX8aaw==";
        public const string Type_ZXFontCatalog      = "#=zqZKzsA47yWa6";

        // ====================================================================
        // 2. Universal Singleton Getter Name
        // ====================================================================
        /// <summary>
        /// Static singleton getter present on ZXGame, ZXSystemGameLevel, ZXSystemMainMenu, ZXGameState, ZXLevelState, etc.
        /// </summary>
        public const string Method_GetCurrentSingleton = "#=zuartwoo=";

        // ====================================================================
        // 3. ZXGame (#=z4RevDP3eECXqXS6JRA==) Members
        // ====================================================================
        public const string Field_ZXGame_AllCommandsList       = "#=zCj$vlp8=";
        public const string Method_ZXGame_Localize             = "#=zBYC_Wew=";
        public const string Method_ZXGame_GetSavesDirectory    = "#=zND5ul2zfzAnWdSnC0A==";
        public const string Method_ZXGame_WriteSaveFile        = "#=zMtGuEM2lBSlZ5BGWvg==";
        public const string Method_ZXGame_ReadSaveFile         = "#=z3YFOTVtBw_rT7oSQRA==";
        public const string Method_ZXGame_SaveGameFlow         = "#=zSV0_oCta8rEv";
        public const string Method_ZXGame_LoadGameAndEnter     = "#=z9IpK6P4YlW8GIY7olg==";
        /// <summary>private static bool ZXGame.#=zGQN$8GM48urIpvI5bA==(string path) — save-file validity gate.
        /// LoadGameAndEnter returns silently (no exception) when this returns false.</summary>
        public const string Method_ZXGame_IsSaveLoadable       = "#=zGQN$8GM48urIpvI5bA==";
        public const string Method_ZXGame_ShowMayorSelection   = "#=zcITiOSD6YNmD";
        public const string Method_ZXGame_ShowSelectedMayors   = "#=zx3_1WsJs4Dyck79oqg==";
        public const string Method_ZXGame_ReturnToMainMenu     = "#=zqF2tlklT$8RE";
        /// <summary>Black full-screen DXObject used as the loader's fade; if already in the scene, LoadGameAndEnter silently no-ops.</summary>
        public const string Field_ZXGame_LoadingFadeObj        = "#=zbvwh$ickzalV";
        /// <summary>Returns true when ZXPlayerModeType is Multiplayer; switches pathfinding to synchronous main-thread.</summary>
        public const string Method_ZXGame_IsMultiplayer        = "#=zB3Vhdin5PerfrubjO4AblAA=";
        /// <summary>Sets ZXPlayerModeType (Singleplayer=0, Multiplayer=1).</summary>
        public const string Method_ZXGame_SetPlayerMode        = "#=znDPORUoLfC4p";
        /// <summary>Public bool true while ZXGame's black loading/transition fade is active.</summary>
        public const string Field_ZXGame_StartupFadeActive     = "#=zTkvm$fc5hNvM";
        /// <summary>Nested BeginWaitCursor/EndWaitCursor reference counter on ZXGame.</summary>
        public const string Field_ZXGame_WaitCursorCount       = "#=z4GoTu3vYw0EXC1201A==";
        /// <summary>Resets wait cursor lock when WaitCursorCount drops to 0 (sets CursorLocked=false, Cursor=Normal).</summary>
        public const string Method_ZXGame_EndWaitCursor        = "#=zIB5ucfBCe7G8";

        // ====================================================================
        // 4. ZXSystemMainMenu (#=zLs$VX1eoAapVflmW_w==) Members
        // ====================================================================
        public const string Field_MainMenu_GridLayout          = "#=zUM4xl3ca4QS35TzMAg==";
        public const string Field_MainMenu_CategoryButtonsDict = "#=zyDj$brKr7icb";
        public const string Field_MainMenu_ReadyForInput       = "#=zBObIu5ds9WvT";
        public const string Field_MainMenu_ContinueButton      = "#=zYxOmgpMMO3Yb";
        public const string Method_MainMenu_SwitchCategory     = "#=zp8c19tQ=";
        /// <summary>Main-thread completion callback of ZXSystemMainMenu (calls Scene.Clear/Scene.Init and sets ReadyForInput).</summary>
        public const string Method_MainMenu_OnLoadComplete     = "#=zwK_pUIeRBpbp";
        public const string Method_MainMenu_OnContinueLoad     = "#=z90VC8V_3LX2x";
        public const string Method_MainMenu_OnNewSurvival      = "#=zxd3DE94B60Abw_zkg7L488DfPg5s";
        public const string Method_MainMenu_OnCustomMap        = "#=zDnLtRmxOqjXPh5h9_Q==";
        public const string NestedEnum_MainMenu_TMenuCategory  = "TMenuCategory";

        // ====================================================================
        // 5. ZXSystemGameLevel (#=zxRcpu6e7NYzT7tGWqPjpOkc=) Members
        // ====================================================================
        public const string Field_LevelSys_PendingCommand      = "#=z0o_eM4o=";
        public const string Field_LevelSys_PendingTarget       = "#=zsP3wQ40=";
        public const string Field_LevelSys_MouseWorldPos       = "#=zetgfbFxpsSuV";
        public const string Field_LevelSys_MouseWorldCell      = "#=zMaiMxP6Lfc1s";
        public const string Field_LevelSys_MouseEntity         = "#=z_QMU7SQiIBfj";
        public const string Field_LevelSys_BuildAreaCursor     = "#=zZ$S0aaS3NQsgmtkOww==";
        public const string Field_LevelSys_CommandsManager     = "#=z4cHfUtNBoiM8";
        public const string Field_LevelSys_SelectionManager    = "#=zCJSdEwbGvSqa";
        public const string Field_LevelSys_PauseButton         = "#=z1pCAbqKOgI6v";
        public const string Field_LevelSys_MiniMap             = "#=zFVczThlAAVvq";
        /// <summary>Private static bool: true during the 6s opening camera zoom animation that overwrites CameraArea each frame.</summary>
        public const string Field_LevelSys_CameraIntroAnim     = "#=zfFXkD0aj1o9gF76ANQ==";

        public const string Method_LevelSys_InitLevel          = "#=zmTU4kueQctVr";
        /// <summary>Main-thread finish action of the async save loader (level fully restored incl. GameTime/entities).</summary>
        public const string Method_LevelSys_OnLevelLoadComplete = "#=zwK_pUIeRBpbp";
        public const string Method_LevelSys_ShowWonderCompleted = "#=zt0ix_4G8xUnfVBHt8A==";
        public const string Method_LevelSys_OnSceneRightClick  = "#=zGxTz6vGn2dxDprCE7A==";
        public const string Method_LevelSys_OnSceneLeftClick   = "#=zX9V39sQYkQxr95dJ7A==";
        public const string Method_LevelSys_ExecutePendingCmd  = "#=zN0jTYBQMVgRf";
        public const string Method_LevelSys_GroupMoveSelected  = "#=zQp2M56bpPd9jmiNkVA==";
        public const string Method_LevelSys_AssignGroupMove    = "#=zgn6bDkSd9MGS3$WvAQ==";
        public const string Method_LevelSys_ShowGroundIndicator= "#=znoo7TyEtVemrA_J6FQ==";       
        public const string Method_LevelSys_OpenGameMenu       = "#=zzka5dmAa0T2CCA26KA==";       
        public const string Method_LevelSys_ShowTopBanner      = "#=zhWLC3K8=";                   
        public const string Method_LevelSys_EnqueueNotification= "#=zYWs2Gv9F3GD5";
        public const string Method_LevelSys_RefreshCmdPanel    = "#=zR1qHCYY=";
        public const string Method_LevelSys_RefreshSelectionGroupsHud = "#=zOAZllFnsly4T";
        public const string Method_LevelSys_ClearPendingBuildMode = "#=z$xo$17_AskhgfUuy9Epz5JQ=";
        public const string Method_LevelSys_TriggerGameEnd     = "#=zIoSB3Q$9ZjUF";
        public const string Method_LevelSys_ShowSurvivalEnd    = "#=zuY2pslm6MYCZNwXKL0J6wWQ=";
        public const string Method_LevelSys_ShowCampaignEnd    = "#=z50KAckqpQjxWzdXGV4uLSoo=";
        public const string Field_LevelSys_GameEndedFlag       = "#=zbcjZqBFFSY96";

        // ====================================================================
        // 6. ZX.Commands.ZXCommand & Build & Train Members
        // ====================================================================
        public const string Method_ZXCommand_GetById           = "#=zqRskexw=";
        public const string Method_ZXCommand_GetByName         = "#=z4koKvss=";
        public const string Method_ZXCommand_GetGeneric        = "#=z5pCZsgI=";
        public const string Method_ZXCommand_Execute           = "#=zfmgzkuw=";
        public const string Method_ZXCommand_CanExecute        = "#=zKc8Kpc_EaFhf";
        public const string Method_ZXCommand_IsVisible         = "#=zXAX1VeKTLe7O";
        public const string Method_ZXCommand_GetResourcesCost  = "#=zcPu1$70ruiRE";
        public const string Method_ZXCommand_CancelExecution   = "#=z9whD4sw=";
        public const string Method_ZXCommand_GetTitle        = "#=zRIE20Dk=";
        public const string Method_ZXCommand_GetDescription  = "#=zN6RTGJU=";
        public const string Method_ZXCommand_GetIcon         = "#=z03r0ANZ63gzr";
        public const string Method_ZXCommand_GetHelp         = "#=zBe_DERidxpqJ";
        public const string Type_ContractMercenary             = "ZX.Commands.ContractMercenary";
        public const string Type_TheInn                        = "ZX.Entities.TheInn";
        public const string Method_TheInn_GetMercenaries       = "#=zsHCRSAjADbRATwM37ZifboM=";
        public const string Method_Build_PlaceBuilding         = "#=zBTwSMLo=";
        public const string Method_BuildOrTrain_GetForType     = "#=zQ4lx63I=";

        // Mayor (ZX.ZXMayor)
        public const string Field_ZXMayor_Registry             = "#=zoTuFkHI=";
        public const string Method_ZXMayor_Apply               = "#=zPTw8CXGE0RqF";

        // ====================================================================
        // 7. ZX.Entities.ZXEntity & Components (CCommandable, CSelectable, CLife)
        // ====================================================================
        public const string Method_ZXEntity_GetCommandable     = "#=zkK_YqS85HrT3g75MJrlvMYo=";
        public const string Method_ZXEntity_GetSelectable      = "#=zTXZoQFXOaWH7";
        public const string Method_ZXEntity_GetLife            = "#=zS6TvFuwF_68l";
        public const string Method_ZXEntity_GetMovable         = "#=zCv3n3xlGOMilHuHlHA==";
        public const string Method_ZXEntity_GetDefaultParams   = "#=zcODCID3ur9P9";
        public const string Method_ZXEntity_RefreshBodyMask    = "#=zIypU1vOCzKav";
        public const string Type_CEntityWatcher                = "ZX.Components.CEntityWatcher";
        public const string Method_CEntityWatcher_UpdateWatch  = "#=zeRH0JCyR0JcPKUO3SQ==";
        public const string Field_CEntityWatcher_TargetMask    = "#=zQuhHE0hijiPHtz8vfQ==";
        public const string Field_CEntityWatcher_TargetEntity  = "#=zzHNTi1HK7zWIwekjb$Gg3RtTI7SD";

        public const string Method_CCommandable_GetCurrentCmd  = "#=zE83e_h4=";
        public const string Method_CCommandable_SetCurrentCmd  = "#=zQKpiMFU=";
        public const string Method_CCommandable_EnqueueOrder   = "#=zRQYIK_o=";
        public const string Method_CCommandable_CancelQueueAt  = "#=z8g7GC7xLrr8j";
        public const string Method_CCommandable_CancelCurrent  = "#=zWGiN6nrTv8Dp";
        public const string Method_CCommandable_AdvanceQueue   = "#=zrz2EI1A=";

        public const string Field_CSelectable_SelectedList     = "#=zIiHCgn_odqH7";
        public const string Method_CSelectable_GetPrimary      = "#=zM3nhRXPdWdhx";
        public const string Method_CSelectable_UnselectAll     = "#=zClknEUY73pQc";

        public const string Field_CMovable_PendingPathRequest  = "#=ziMavkfK9ZJji";

        // ====================================================================
        // 8. ZX.ZXLevelState Members
        // ====================================================================
        public const string Method_ZXLevelState_SaveTimeState  = "#=zQXHqcVh9mGZZ";
        public const string Method_ZXLevelState_RestoreTime    = "#=zF1YHTRUZSNRa";
        public const string Method_ZXLevelState_ConsumeRes     = "#=z5YtS5bwlM1kX";
        public const string Method_ZXLevelState_RefundRes      = "#=zG0slAM4=";
        public const string Method_ZXLevelState_CanAffordRes   = "#=zh4x6WKNtqKnr";
        public const string Method_ZXLevelState_AvailFood      = "#=z7SWdP68KoEfdgSB1Ww==";
        public const string Method_ZXLevelState_AvailWorkers   = "#=z1GVm3ppkkMeC";
        public const string Method_ZXLevelState_AvailEnergy    = "#=zkqSYgCu1efoDkViT0w==";
        public const string Method_ZXLevelState_GetDXLevel     = "#=zOHDY2QTzgRSB";
        public const string Method_ZXLevelState_ConsumeBonus   = "#=z0b0u0bUCzEHXjLBj5A==";
        public const string Method_ZXLevelState_GetBonusCount  = "#=zBoOUVPKgrQd5bNC4RA==";
        public const string Method_ZXLevelState_GetSelectionGroups = "#=zjt55PivoReTlHchOXA==";
        public const string Method_ZXLevelState_RecalcResources = "#=zgW9mPqUC4s5b";

        // ====================================================================
        // 9. GUI & MessageBox Helpers
        // ====================================================================
        public const string Method_MsgBox_ShowAlert            = "#=zcvkFXLt$qEo6";
        public const string Method_MsgBox_ShowOkCancel         = "#=zy6vF6BSAUKyT";
        public const string Method_MsgBox_ShowYesNo            = "#=zFNDB6pz7Wqg3";
        public const string Method_MsgBox_ShowInputBox         = "#=znd_PcbSNuH79";
        public const string Method_MsgBox_ShowListSelector     = "#=zYjeCpah9Jpv2S1i8lQ==";
        public const string Method_MsgBox_GetPlainTextRenderer = "#=zth32r9N3TXbR";

        public const string Method_ZXWindow_AddBottomButton    = "#=zJMx0W0Q=";
        public const string Method_ZXWindow_ShowWindow         = "#=zyruJg$0=";
        public const string Method_ZXWindow_CloseWindow        = "#=zePbLN$s=";
        public const string Type_ZXFontCatalogFonts            = "#=zqZKzsA47yWa6+#=zrtNti8Q=";
        public const string Method_Font_Heading                = "#=z135pFLu2OAFw";
        public const string Method_Font_Secondary              = "#=zwi063I7$xiwT";
        public const string Method_Font_Body                   = "#=zKEARtzqGim$i";
        public const string Method_Font_Plain                  = "#=z1tQEbFB9gJlB";
        public const string Method_OMiniMap_PingCell           = "#=zvr$ku1Q=";
        public const string Method_SteamMgr_RunCallbacks       = "#=zVnIv8xS3EOZr";

        // ====================================================================
        // 10. ZXSystemAudio (#=zJYbPa4gP2VI16zEh5y2Zxew=) Synchronous Sound Playback
        // ====================================================================
        public const string Field_Audio_Current              = "#=zQFWYIvs=";
        public const string Method_Audio_PickFromList        = "#=zas_QVHmVp4jw4ljVfQ==";
        public const string Method_Audio_PickWeightedVoice   = "#=zGKbBBAxDUE94EUjCrw==";
        public const string Method_Audio_StartAmbient        = "#=zbJZeOtklBmAP9GRRRA==";
        public const string Method_Audio_PlayUnique          = "#=zEdvWTpVsmit5o27Ihg==";
        public const string Method_Audio_PlayEntitySound     = "#=zSH8FzcZvhKUrpnRPVw==";
        public const string Method_Audio_PlayPosSound        = "#=zOgrlHVlq5fSBuzdvAQ==";
        public const string Method_Audio_PlayPosSound3D      = "#=zax3sPZAXipzMtWW$kMS6B58=";
        public const string Method_Audio_Play2DSound         = "#=zoEMwpnZ2M_6d";
        public const string Method_Audio_PlayNamed2DSound    = "#=z_mJawUihZGwsBo$jFQ==";
        public const string Method_Audio_PlayNotification    = "#=z1J0LzK2PeziK";

        // ====================================================================
        // 11. ZX.Systems.SysColonists Deterministic Reset
        // ====================================================================
        public const string Type_SysColonists                = "ZX.Systems.SysColonists";
        public const string Field_SysColonists_Cursor        = "#=zUJ8sfNZq6x5U";
        public const string Field_SysColonists_List          = "#=zTsexjauQRm5IZtGeraXJooM=";
        public const string Method_SysColonists_GetCurrent   = "#=zuartwoo=";
        public const string Method_SysColonists_RebuildList  = "#=zt0FcM$Mg6$EPDdIcLavKxd4=";

        // ====================================================================
        // 12. BHFarmer Deterministic Wheat Cell Selection
        // ====================================================================
        public const string Type_TerrainSystem               = "#=zDB$fZzE4fVJzNeDCt$4XVmDIV3Ul";
        public const string Method_TerrainSystem_GetCurrent  = "#=zuartwoo=";
        public const string Prop_TerrainSystem_Cells         = "TerrainResourceCells";
        public const string Type_ZXBehaviour                 = "ZX.Behaviours.ZXBehaviour";
        public const string Field_ZXBehaviour_PickFarmCell   = "#=zK2CGzIZdW6uI$Q9jPNxMDPs=";
        public const string Method_BData_GetOwner            = "#=zHKPHAW92Cyls";
        public const string Method_ZXEntity_GetParams        = "#=zcODCID3ur9P9";

        // ====================================================================
        // 13. Zombie Aggro / Alerting State (for Desync Tolerance)
        // ====================================================================
        public const string Type_Zombie                      = "ZX.Entities.Zombie";
        public const string Method_ZXEntity_GetBehaviour     = "#=zrPViR3ZjTZsjVndIuu8$9io=";
        public const string Field_BData_TargetRef            = "#=zP9AEykk6714q";
        public const string Method_ZXEntity_CanPlaceAtCell   = "#=z2IRuTX3OAzJgkAHNDKW65bN9UxnV";
        public const string Method_ZXEntity_UpdateCellPosition = "#=zthu8vuk=";

        // ====================================================================
        // 14. Mayor Choosing State on ZX.ZXLevelState
        // ====================================================================
        public const string Method_ZXLevelState_IsChoosingMayor  = "#=zL2Yzn7nYwjqmjuOHNGOPtmw=";
        public const string Method_ZXLevelState_SetChoosingMayor = "#=zNc5a0Zvt_w5ui5CfZqiEAkE=";

        // ====================================================================
        // 15. Random Survival Map Generator & Balance Scaling
        // ====================================================================
        public const string Type_MapGenerator                  = "#=zyl_NPjjlA7DRfVtsRJCX1kN4BxSr";
        public const string Method_MapGenerator_Generate       = "#=zEzgd90E=";
        public const string Type_ZXRandomLevelParams           = "ZX.GameSystems.ZXRandomLevelParams";
        public const string Type_ZXMapTheme                    = "ZX.GameSystems.ZXMapTheme";
        public const string Method_ZXMapTheme_GetThemes        = "#=z4k5FO$EclQhr";
        public const string Type_ZXLevelExtension              = "ZX.GameSystems.ZXLevelExtension";
        public const string Type_ZXMapDrawer                   = "ZX.GameSystems.ZXMapDrawer";
        public const string Type_ZXLevelEvent                  = "ZX.GameSystems.ZXLevelEvent";
        public const string Method_ZXLevelEvent_ScalePopulation = "#=z0XSLMcBEuHC4";
        public const string Type_CommandCenter                 = "ZX.Entities.CommandCenter";
        public const string Type_Ranger                        = "ZX.Entities.Ranger";
        public const string Type_SoldierRegular                = "ZX.Entities.SoldierRegular";
        public const string Method_LevelSys_CapturePanorama    = "#=z5NVIUf3bnsHNNo8FfA==";
        public const string Method_LevelSys_EnterGameView      = "#=zwK_pUIeRBpbp";
        public const string Method_ZXGameState_SetCurrent      = "#=z2SXmL2Q=";
        public const string Method_ZXLevelState_SetCurrent     = "#=z2SXmL2Q=";
        public const string Method_ZXLevelState_PreInit        = "#=zF1YHTRUZSNRa";
        public const string Method_ZXGame_ResetWorldState      = "#=zFi4bPug=";
        public const string Method_ZXGame_AsyncTransition      = "#=z9DPDdq9qP9lZ";
        public const string Type_ProgressWindowHelper          = "#=zzi2cl4h1HYA5cX8aaw==";
        public const string Field_ProgressWindow_Current       = "#=z9gBNlmJCE1ly";
        public const string Field_LevelSys_IntroCinematic      = "#=zfFXkD0aj1o9gF76ANQ==";
        public const string Field_LevelSys_ScreenAnim          = "#=z40N_lqqB8daJ";

        // ====================================================================
        // 16. Keyboard Bindings Manager (#=zyMMweQL9LzVp8ZCJ7w==)
        // ====================================================================
        public const string Type_KeyboardBindingsManager       = "#=zyMMweQL9LzVp8ZCJ7w==";
        public const string Method_Keyboard_IsKeyForCommand  = "#=zRtXKxIY=";

        // ====================================================================
        // 17. Phase 1 Async-Align Noise & Desync Isolation Symbols
        // ====================================================================
        public const string Type_Colonist                      = "ZX.Entities.Colonist";
        public const string Type_Building                      = "ZX.Entities.Building";
        public const string Method_Building_SpawnColonists     = "#=z0RIVd3YfBl848VD92A==";
        public const string Type_OCellAreaCursor               = "ZX.GUI.OCellAreaCursor";
        public const string Field_OCellAreaCursor_LastEnergyCell = "#=zOgOjDSANK0WHtgVX_wzbxBg=";
        public const string Method_OCellAreaCursor_ClearEnergyPreview = "#=zglihyOZQi982dUVN0g==";
        public const string Method_OCellAreaCursor_UpdateVisual = "#=zwffgY1s=";

        // ====================================================================
        // 18. Phase 2 & 3 Async Delta Patching Symbols
        // ====================================================================
        public const string Type_ZXOrder                       = "ZX.Commands.ZXOrder";
        public const string Type_CBuildable                    = "ZX.Components.CBuildable";
        public const string Method_Building_RecalcResources    = "#=z3rO8Jok=";
        public const string Method_Structure_UpdateEnabled     = "#=zUBPru1Ew_PxN";
        public const string Method_LevelState_RecordEntityCreated = "#=zw0DWH2yCwj1E";

        // ====================================================================
        // 19. Phase 4 M1 Multi-Faction Mode Symbols
        // ====================================================================
        public const string Method_MapLayers_RebuildEnergyGrid     = "#=zK$nELVArHhqXXa$0lA==";
        public const string Method_MapLayers_InitEnergyConnectivity = "#=z9ok_i231SPp6b3Ux7A==";
        public const string Method_MapLayers_UpdateEnergyCells     = "#=zMxZdonzH58cOsMui6Q==";
        public const string Method_MapLayers_HasPathToAnyCC        = "#=zs1C_pLjYHbdEceIG1y8dYemfTA9s";
        public const string Method_MapLayers_HasPathBetween        = "#=z3gBRVDQqtvENQXOHNg==";
        public const string Field_MapLayers_EnergyGrid             = "#=zVXTH1ahoIxQNg$omKQ==";
        public const string Field_MapLayers_EnergyFrontier         = "#=zwfOTOoiNSnzaMdxGOw==";
        public const string Field_MapLayers_EnergySources          = "#=zzdYbmlOsMh5Qdai_1wg_GcVnp1uyXUY2Jg==";
        public const string Field_MapLayers_EnergyLastIdx          = "#=zI1XwqvydMOklwONBSr1AZauxHESdsKz87A==";
        public const string Field_MapLayers_EnergyInitDone         = "#=z6CNMVnZljsdxmXV9CUnk0EGQyUQH$u7hZA==";
        public const string Field_MapLayers_EnergyLock             = "#=zEj$Wpr4DFANo4O0eAQ==";
        public const string Type_CEnergyTransferer                 = "ZX.Components.CEnergyTransferer";
        public const string Method_CEnergyTransferer_CheckDisconnected = "#=zeJrYi6vVVMO8Q25qqH7ixNY=";
        public const string Method_CEnergyTransferer_UpdateGrid    = "#=zB_XAZFXeOC5f5keLMg==";
        public const string Field_Building_HasEnergyConnection     = "#=zPWtiBZDTAwpyLcsK2w==";
        public const string Method_Building_RefreshEnergy          = "#=zDGe0WvyHfDUGJ1AUiA==";
        public const string Method_LevelSys_IsCellPlayableForEnergy = "#=z9OggxN$iWkcVshXOAQ==";
        public const string Field_ZXLevelState_CachedCC            = "#=zWcIPez8Xawjo";
        public const string Method_ZXLevelState_RefreshCCCell      = "#=z1lX7NrLe$yjs";
        public const string Field_LevelSys_CurrentCC               = "#=zWcIPez8Xawjo";
        public const string Method_CSelectable_SetSelectedInternal = "#=zh0kwu2g=";
        public const string Type_Train                             = "ZX.Commands.Train";
        public const string Type_Upgrade                           = "ZX.Commands.Upgrade";
        public const string Method_Command_OnComplete              = "#=znpSJWcQ=";
        public const string Method_Command_ExecuteImpl             = "#=zfMM7feQ=";
        public const string Method_ZXEntity_TransferUpgradeState   = "#=z7k$1Q4oQNHe8";
        public const string Type_CNeutralJoinable                  = "ZX.Components.CNeutralJoinable";
        public const string Method_CNeutralJoinable_Update         = "#=z5GPRaBw=";
        public const string Type_CLife                             = "ZX.Components.CLife";
        public const string Method_CLife_Update                    = "#=zzHXxhbjNa0Zf";
        public const string Method_LevelState_RecordEntityDead     = "#=zmGjBHwC$KPGs";
        public const string Field_ZXBehaviour_PickRoamingCell      = "#=zxLay8zPafKGD";
        public const string Field_ZXBehaviour_SetInfectedDestiny   = "#=zxSb56aPcvAt3Ruzqaw==";
        public const string Field_ZXBehaviour_SetInfectedDestiny2  = "#=zovJW5KjKdU4o";
        public const string Field_ZXBehaviour_TargetCurrentCC      = "#=zVrYo0VGEG7Sj";
        public const string Method_ZXBehaviour_FindBuildingOrCC    = "#=zTICDnCdrTfea";
        public const string Method_ZXBehaviour_FindEnabledBldgOrCC = "#=zWl4GSKQNpFW2";

        // ====================================================================
        // 20. Phase 4 M2 & M3 Multi-Faction Economy, Tech, Wonders & Visuals
        // ====================================================================
        public const string Field_LevelState_ResourceStorage       = "#=z$AJASl1AiK8t";
        public const string Field_LevelState_FoodSupply            = "#=zVBTXbFQWx5nv";
        public const string Field_LevelState_WorkersSupply         = "#=zEfI2uaoUZ782";
        public const string Field_LevelState_EnergySupply          = "#=zaZY8fVVs7KkTcrzcGA==";
        public const string Field_LevelState_FoodNeeded            = "#=zjc0VGTlxmAwC";
        public const string Field_LevelState_WorkersNeeded         = "#=zT3_qxWviRzqs";
        public const string Field_LevelState_EnergyNeeded          = "#=zFQpg_1MTtOinPniq0A==";
        public const string Field_LevelState_Colonists             = "#=zjHykNU3M_aZ44GcRCw==";
        public const string Field_LevelState_GoldByCC              = "#=zTW_7s8UG6Tbcne31hQ==";
        public const string Field_LevelState_TotalStock            = "#=zotIDJniiBmaD";
        public const string Method_LevelState_Recalculate          = "#=zBpC6By32LAqv";
        public const string Method_LevelState_8hStep               = "#=zgLWBIL0dUIT9ZcCNSQ==";
        public const string Method_LevelState_SetEnhancerBuildings = "#=zxHjqi6_iced29ognGGOgp5TMC9stiUDrhg==";
        public const string Method_LevelState_GetEnhancerBuildings = "#=zDsPVU4dXlejQD9RrXz8QIy8Ssk_1CySn7g==";
        public const string Method_LevelState_AddWoodWithMarket    = "#=zpo2hsxCgQn3I";
        public const string Method_LevelState_AddStoneWithMarket   = "#=zKBFI1SS2BzHt";
        public const string Method_LevelState_AddIronWithMarket    = "#=zynafIZs0wZbU";
        public const string Method_LevelState_AddOilWithMarket     = "#=zOgTGrsIA9_ZE";
        public const string Method_LevelState_AddGoldDirect        = "#=z7dTWXp0=";
        public const string Method_LevelState_AddCost              = "#=zG0slAM4=";
        public const string Method_LevelState_ConsumeCost          = "#=z5YtS5bwlM1kX";
        public const string Method_LevelState_AddOnlyOneExecuted   = "#=zNHCibbkaY4C1bNgYnQ==";
        public const string Method_LevelState_HasOnlyOneExecuted   = "#=zNGgzojiiQIq14QwoDQ==";
        public const string Method_LevelState_GetSellFactorWood    = "#=zTNxqKnSaBBRFMynxNZxc7qI=";
        public const string Method_LevelState_GetSellFactorStone   = "#=zg4iNKCARS_A6O86uYq_VKlKqZ8a_";
        public const string Method_LevelState_GetSellFactorIron    = "#=zLKYHUMak3IMsBXqDgwvXP1w=";
        public const string Method_LevelState_GetSellFactorOil     = "#=zvzbRbTT3bnirsVecV8tTGAo=";
        public const string Method_ZXGame_GetEntityName          = "#=zEM8NoV67DXOL";
        public const string Type_CResourceGenerator                = "ZX.Components.CResourceGenerator";
        public const string Method_CResourceGenerator_Update       = "#=zzHXxhbjNa0Zf";
        public const string Type_PickableItem                      = "ZX.Entities.PickableItem";
        public const string Method_PickableItem_Pick               = "#=zmrUbwuk=";
        public const string Type_Technology                        = "ZX.Commands.Technology";
        public const string Type_UndoBuilding                      = "ZX.Commands.UndoBuilding";
        public const string Type_Destroy                           = "ZX.Commands.Destroy";
        public const string Method_ZXCommand_GetCost               = "#=zcPu1$70ruiRE";
        public const string Method_ZXCommand_CancelRefund          = "#=zTx92yEE=";
        public const string Method_ZXCommand_CancelProgress        = "#=z9whD4sw=";
        public const string Method_ZXMayor_LoadAll                 = "#=zl1IE4Os2suIF";
        public const string Field_ZXEntity_CachedMinimapColor      = "#=zK_R0cmV1aBjs";
        public const string Field_ZXEntity_BaseMinimapColor        = "#=zd_y1feWnG0usZCJzLQ==";
        public const string Field_Building_ResourceCost            = "#=zXbxJzH8=";

        // ====================================================================
        // 21. Phase 4 M4 Multi-Base Map Generator & Multi-Wave Swarms
        // ====================================================================
        public const string Method_LevelEvent_PickGenerators       = "#=zUMCW0sQ=";
        public const string Method_LevelEvent_Trigger              = "#=zrITPs$A=";
        public const string Method_LevelEvent_BuildEntityCounts    = "#=zHI6D76EGh68R";
        public const string Method_LevelEvent_BuildMessage         = "#=z9kNBieQ=";
        public const string Method_LevelEvent_Notify               = "#=zBAspRIo=";
        public const string Type_CUnitGenerator                    = "ZX.Components.CUnitGenerator";
        public const string Method_CUnitGenerator_Update           = "#=zzHXxhbjNa0Zf";
        public const string Method_CUnitGenerator_AddRequest       = "#=zERrxnH$byRKd";
        public const string Method_GenerationRequest_GetTemplate   = "#=zgkfLCBPOBsM2";
        public const string Method_GenerationRequest_SetTemplate   = "#=zloP7bF56hnHU";
        public const string Type_CInfectionNest                    = "ZX.Components.CInfectionNest";
        public const string Type_CMiniMapInfo                      = "ZX.Components.CMiniMapInfo";
        public const string Method_CMiniMapInfo_GetConfig          = "#=zyohOqc5Ng1Am";
        public const string Type_EntityTemplateCatalog             = "#=zYLpZK4K9zSHp";
        public const string NestedType_Catalog_Buildings           = "#=zyx9SpHmg_Pj8YUXUOQ==";
        public const string NestedType_Catalog_Zombies             = "#=zwwyTj9$ODjUy";
        public const string Method_Catalog_DoomNestLarge           = "#=zFvWYnEaYcTiwWWz6pA==";
        public const string Method_Catalog_DoomNestMedium          = "#=zdObI08b0EDdG0TaPCw==";
        public const string Method_Catalog_DoomNestSmall           = "#=zAr9Fw3PhVc7FFNTBvw==";
        public const string Method_Catalog_ZombieGiant             = "#=zq6F4jtPoQUZV263PTQ==";
        public const string Method_Catalog_ZombieMutant            = "#=zETCVfaScp7p0jCA9Ag==";
        public const string Method_Catalog_ZombieStrongA           = "#=zEMa9Cy26vxmi";
        public const string Method_Catalog_ZombieVenom             = "#=zTuDKvlVxpn5uBRPB2A==";
        public const string Method_Catalog_ZombieHarpy             = "#=z_1JbAdKCTe7jAmUh4A==";
        public const string Type_ZombieGiant                       = "ZX.Entities.ZombieGiant";
        public const string Type_ZombieMutant                      = "ZX.Entities.ZombieMutant";
        public const string Type_ZombieStrongA                     = "ZX.Entities.ZombieStrongA";
        public const string Type_ZombieVenom                       = "ZX.Entities.ZombieVenom";
        public const string Type_ZombieHarpy                       = "ZX.Entities.ZombieHarpy";
        public const string Type_ZombieDressedA                    = "ZX.Entities.ZombieDressedA";
        public const string Type_ZombieWeakA                       = "ZX.Entities.ZombieWeakA";
        public const string Type_ZombieMediumA                     = "ZX.Entities.ZombieMediumA";
        public const string Type_ZombieWorkerA                     = "ZX.Entities.ZombieWorkerA";

        // ====================================================================
        // 22. Deterministic Fog-of-War & Watcher Cadence Isolation
        // ====================================================================
        public const string Method_ZXBehaviour_WatchIntervalGate   = "#=z5O$3wVJdMoVTAfFU1A==";
        public const string Type_CMapWatcher                       = "ZX.Components.CMapWatcher";
        public const string Field_CMapWatcher_WatchRange           = "#=z5A00j9MXV3op";
        public const string Type_CInvisibleWhenNotWatched          = "ZX.Components.CInvisibleWhenNotWatched";
        public const string Method_CInvisibleWhenNotWatched_UpdateOpacity = "#=zeSvSmIE=";
        public const string Field_MapLayers_FogEnabled             = "#=zAWAnTYtmVWSWf5QWuplIscs=";
        public const string Method_MapLayers_RevealCellRadius      = "#=z$YIzGtguLyaT";
        public const string Method_MapLayers_IsExploredPoint       = "#=zm1sFXg4ILBZxoivUvA==";
        public const string Method_MapLayers_IsWatchedPoint        = "#=z4SfTR0oOSoLp";
        public const string Method_MapLayers_IsNearExplored        = "#=zdnaioeiQZgd6TvCsa2ua8_w=";
    }
}
