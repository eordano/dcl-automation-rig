## OTP Code Entry Screen (Email Login) - Capture Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Reached after user submits email on LoginSelectionAuthView -> FSM enters IdentityVerificationOTPAuthState -> VerificationOTPAuthView.Show() displays the OTP input screen
- driver: 1. LOCATE controller via reflection:
   - Retrieve MVCManager from dynamicWorldContainer
   - Call FindControllerByTypeName(mvc, "AuthenticationScreenController") to get authCtl
   - Retrieve authCtl.CurrentState property (ReactiveProperty<AuthStatus>)

2. TRANSITION into OTP screen programmatically:
   - Already in Play mode with cached "Evaristo" identity
   - Set debugSettings.showAuthentication=true (done in Arm phase)
   - MainSceneLoader.Awake() bootstrap sees showAuth, creates AuthenticationScreenView
   - AuthenticationScreenController.OnViewInstantiated() creates FSM with IdentityVeri
- dataNeeds: Test account with email + valid phone (or mock OTP server); Evaristo cached identity must be expired/missing OR auto-login must be disabled in test mo

## Web3/Dapp Verify Screen (wallet connection code + countdown)
- feasible: needs-data | ctx: in-world-session | conf: medium
- controller/view: 
- trigger: User initiates Dapp wallet login (MetaMask/WalletConnect/etc) from LoginSelectionAuthView; backend sends verification code via VerificationRequired event; VerificationDappAuthView renders code + count
- driver: 1. Locate AuthenticationScreenController via MVCManager (existing harness code at line 303): object loader = FindMainSceneLoader(); object dyn = GetPrivateField(loader, "dynamicWorldContainer"); object mvc = GetPublicProperty(dyn, "MvcManager"); object authCtl = FindControllerByTypeName(mvc, "AuthenticationScreenController"). 2. Verify authCtl != null and read CurrentState property via GetMember(authCtl, "CurrentState") -> ReactiveProperty<AuthStatus>. 3. From LoginSelectionAuthState.cs line 58-64, programmatically invoke wallet login by calling the private Login(LoginMethod.METAMASK) method o
- dataNeeds: Live Dapp authentication backend running and issuing real verification codes via VerificationRequired event. The cached "Evaristo" identity used by th

## web3confirm
- feasible: needs-data | ctx: in-world-session | conf: medium
- controller/view: 
- trigger: A wallet action (personal_sign, eth_signTypedData_v4, or eth_sendTransaction) prompts signing, triggering TransactionConfirmationDelegate callback that shows Web3ConfirmationPopupView
- driver: CONCRETE DRIVER RECIPE (reflection-preferred):

**VIEW CLASS:** DCL.AuthenticationScreenFlow.Web3ConfirmationPopupView
  - File: Explorer/Assets/DCL/AuthenticationScreenFlow/Views/Web3ConfirmationPopupView.cs:20
  - Public method: UniTask<bool> ShowAsync(TransactionConfirmationRequest request) [line 59]
  - NOT an MVC controller — it's a standalone View instantiated by Web3AuthenticationPlugin and registered as a callback

**TRIGGER FLOW (how it appears at runtime):**
  1. A wallet action calls ethereumApi.SendAsync(EthApiRequest request, Web3RequestSourc
- dataNeeds: 
1. **Wallet account with signing capability**: Test account 'Evaristo' (already cached in editor) must have an active ThirdWeb wallet connection init

## lobbynew - Capture NEW Account Onboarding Lobby Screen
- feasible: needs-data | ctx: in-world-session | conf: medium
- controller/view: 
- trigger: Reach via profile fetch → new account path (no existing profile in catalyst); avatar randomizer, name input, body type selector, terms toggle, finalize button shown
- driver: 1. Locate AuthenticationScreenController via reflection: mvcManager.Controllers.Values → find type name "AuthenticationScreenController" (harness method FindControllerByTypeName at file:1086)
2. Access its private fsm field (MVCStateMachine<AuthStateBase>) via GetPrivateField(authCtl, "fsm")
3. Construct the entry payload:
   - Get current cached identity via GetPrivateField(authCtl, "web3Authenticator") → query for IWeb3Identity.Address
   - Create Profile via Profile.NewRandomProfile(identityAddress.ToString()) [Profile.cs:149]
   - Email = empty string or sourced from most recent OTP state 
- dataNeeds: Requires the test account to have reached OTP email verification step with a FRESH wallet (no prior profile). The "Evaristo" cached identity must eith

## loading: Loading tip carousel during post-auth initial world load
- feasible: yes-automated | ctx: auth-cached | conf: high
- controller/view: 
- trigger: Shown after authentication completes; LoadingScreen.ShowWhileExecuteTaskAsync() invokes SceneLoadingScreenController via MVC during world profile/avatar/landscape load
- driver: 1. After auth state reaches AuthStatus.LoggedIn or LoggedInCached and authentication screen closes.
2. Reflection chain to invoke:
   - Get dynamicWorldContainer.MvcManager (object mvc)
   - Create: CancellationToken ct = CancellationToken.None
   - Create: AsyncLoadProcessReport report = AsyncLoadProcessReport.Create(ct) via reflection on AsyncLoadProcessReport.Create(CancellationToken) static method at AsyncLoadProcessReport.cs:113
   - Get Type: SceneLoadingScreenControllerType = FindType("DCL.SceneLoadingScreens.SceneLoadingScreenController")
   - Get Type: ParamsType = FindType("DCL.Scene
- dataNeeds: none — the cached Evaristo identity auto-logs in (no wallet/OTP required); AsyncLoadProcessReport is created empty and progress-driven by the scene lo

## sceneloading
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: DCL.SceneLoadingScreens.SceneLoadingScreenController / DCL.SceneLoadingScreens.SceneLoadingScreenView @ Explorer/Assets/DCL/SceneLoadingScreens/SceneLoadingScree
- trigger: Scene loading screen shown during teleport via RealmNavigator.TeleportToParcelAsync → LoadingScreen.ShowWhileExecuteTaskAsync → MVCManager.ShowAsync(SceneLoadingScreenController.IssueCommand)
- driver: 
1. Obtain mvcManager via reflection: dynamicContainer.MvcManager (existing harness pattern line 509)
2. Create AsyncLoadProcessReport via reflection:
   - Type: FindType("DCL.Utilities.AsyncLoadProcessReport")
   - Factory: reportType.GetMethod("Create", BindingFlags.Public|Static).Invoke(null, new[] {CancellationToken.None})
   - Complete immediately: SetProgress(0f), then SetProgress(1f) to ensure settled state
3. Construct SceneLoadingScreenController.Params via reflection:
   - Type: FindType("DCL.SceneLoadingScreens.SceneLoadingScreenController+Params")
   - Ctor: paramsType.GetConstruct
- dataNeeds: none

## minspecs
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Minimum-specs guard screen shown during app bootstrap (after DI init, before realm load) when hardware fails platform-specific spec checks; forced via --forceMinimumSpecsScreen app flag.
- driver: 1. Arm harness mode="minspecs" in SessionState, set debugSettings.showAuthentication=false, EnterPlaymode (domain reload)
2. In RunMinSpecsCoroutine (running post-reload): wait ~30s for MainSceneLoader.VerifyMinimumHardwareRequirementMetAsync() (MainSceneLoader.cs:337,459) to instantiate+show MinimumSpecsScreenController
3. Locate controller via reflection: FindMainSceneLoader() → GetPrivateField("dynamicWorldContainer") → GetPublicProperty("MvcManager") → FindControllerByTypeName(mvc, "DCL.ApplicationMinimumSpecsGuard.MinimumSpecsScreenController") → lookup in MvcManager.Controllers dict by c
- dataNeeds: none

## Update Required Modal - "updaterequired" - LauncherRedirectionScreenController
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Version mismatch detected during InitialGuardsCheckSuccessAsync; shows when currentVersion.Version.IsOlderThan(latestVersion) within DoesApplicationRequireVersionUpdateAsync
- driver: STEP 1: Construct version mismatch condition via app args
- Set debugSettings.EnableVersionUpdateGuard = true (or pass SKIP_VERSION_CHECK=false in app args)
- Pass SIMULATE_VERSION="1.0.0" to DCLVersion.FromAppArgs (line 605 MainSceneLoader.cs:587) so it's older than latest
- This causes IsOlderThan() check at line 601 to return true

STEP 2: Mock the ApplicationVersionGuard.GetLatestVersionAsync to return newer version
- Instantiate ApplicationVersionGuard with mocked IWebRequestController (line 598)
- Mock GetLatestVersionAsync to return "999.0.0" (or any version > simulated version)
- Alter
- dataNeeds: none - all required data is constructed via version simulation (SIMULATE_VERSION app arg) and mocked web responses; no user-specific data (wallet, NFT

## Capture driver for "connectionerror" — Connection Error modal (Timeout state)
- feasible: needs-data | ctx: in-world-session | conf: high
- controller/view: DCL.UI.ErrorPopup.ErrorPopupWithRetryController : ControllerBase&lt;ErrorPopupWithRetryView, ErrorPopupWithRetryController.Input&gt; at Explorer/Assets/DCL/UI/Er
- trigger: Connection Error modal shown during RealUserInAppInitializationFlow when a TaskError.Timeout occurs during initialization operations (livekit handshake, realm setup, or loading exceeds timeout window)
- driver: 1. REFLECTION PATH (preferred):
   - At runtime, after entering Play mode with debugSettings.showAuthentication=false (skip auth):
   - Locate IMVCManager via: mainSceneLoader -> dynamicWorldContainer (private field) -> MvcManager (public property)
   - Call ErrorPopupWithRetryController.IssueCommand(new ErrorPopupWithRetryController.Input(
       title: "Connection Error",
       description: "We were unable to connect to Decentraland. Please verify your connection and retry.",
       iconType: ErrorPopupWithRetryController.IconType.CONNECTION_LOST,
       retryText: "Continue"
     ))
   - I
- dataNeeds: 
CRITICAL: The test account "Evaristo" (the default cached identity in editor) likely has no special data requirements to *show* the popup via reflect

## Expanded Chat Window (chatwindow) - Capture Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Transition the persistent chat panel from default/minimized state to expanded (focused) state via ChatStateMachine.SetFocusState()
- driver: 1. Access the singleton ChatOpener.Instance (DCL.Chat.ChatOpener, marked [Singleton] at Explorer/Assets/DCL/Chat/ChatOpener.cs:7).

2. Call ChatOpener.Instance.CloseAllViewsAndFocusChat() (Explorer/Assets/DCL/Chat/ChatOpener.cs:31-35). This method:
   - Closes all non-persistent MVC views via mvcManager.CloseAllNonPersistentViews()
   - Publishes ChatEvents.FocusRequestedEvent via chatEventBus.RaiseFocusRequestedEvent()

3. The event flows: ChatEvents.FocusRequestedEvent → ChatMainSharedAreaController.SetFocusState()
- dataNeeds: none - ChatOpener.CloseAllViewsAndFocusChat() requires no constructor params, only the singleton instance

## Chat Profile Hovercard Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: GenericUserProfileContextMenuController (controls GenericContextMenuView with GenericContextMenuUserProfileView child control) @ Explorer/Assets/DCL/UI/GenericCo
- trigger: Click on a username in a chat message to open the user profile context menu hovercard
- driver: 1. **Obtain a test conversation with a real user**:
   - Use `ChatOpener.Instance.OpenPrivateConversationWithUserId(friendUserId)` to open a DM with a friend
   - Wait for chat messages to load (poll `CurrentChannelService.CurrentChannel` via reflection)
   - Extract the first non-self user's wallet address from a message: `chatMessage.SenderWalletAddress`

2. **Programmatically trigger the context menu via reflection** (avoiding input simulation):
   - Retrieve `ChatContextMenuService` instance via MainSceneLoader → DI container reflection
   - Get a test user's address (e.g., from online pla
- dataNeeds: A real friend's wallet address (userId). The test account 'Evaristo' must have at least one friend OR be in an active DM with another user to see mess

## Reaction Pills + Picker (HUD Screen Capture Driver)
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: User hovers a chat message in the HUD panel, clicks the reaction button that appears, and the emoji picker bar + existing reaction pills below the message render.
- driver: 1) Reflection-locate MainSceneLoader.dynamicWorldContainer.MvcManager + MainSceneLoader.staticContainer.LoadingStatus; wait for LoadingStage="Completed" (file: DclPlaytestHarness.cs:362-412). 2) Via MvcManager.Controllers dict, find ChatPanelPresenter key and get .View (ChatPanelView). 3) Ensure NEARBY channel has a test message: if needed, invoke IChatMessagesBus.Send(NEARBY_CHANNEL, "Test", ChatMessageOrigin.Chat, timestamp) (file: DclPlaytestHarness.cs:776-797). 4) Reflection into ChatPanelView.MessageFeedView.loopList (LoopListView2) to enumerate visible ChatEntryView children; pick any me
- dataNeeds: Test account "Evaristo" identity (auto-loaded in editor via MainSceneLoader), NEARBY channel (always available), one chat message (can generate via Tr

## inputsuggestions - @mention/:emoji: autocomplete popup
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Type @ or : in the chat input field to trigger the suggestion panel overlay with filtered profile names or emoji suggestions.
- driver: 1. Show the Chat panel: Call mvcManager.ShowAsync<ChatMainSharedAreaView,ControllerNoData>(ChatMainSharedAreaController.IssueCommand(), ct) via reflection. This returns a UniTask which must be awaited via GetAwaiter/IsCompleted poll (see harness example lines 1052-1083).

2. After chat settles (yield/poll until not animating), acquire the InputSuggestionPanelView instance from ChatMainSharedAreaView.ChatPanelView.InputView.suggestionPanel via reflection (nested property chain).

3. Trigger @ mention suggestions: Programmatically call ChatInputView.inputField.SetTextWithoutNotify("@test") to se
- dataNeeds: none - the test account Evaristo auto-loads and can immediately type in chat. Emoji dictionary is built into EmojiMapping from emojiPanelConfiguration

## voice
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Click the NearbyVoiceChatButton in the sidebar to show the Voice-chat HUD panel via MVC
- driver: 1. Verify IslandRoom (Live Kit) is connected: attempt to invoke NearbyVoiceChatStateModel.Enable() via the DynamicWorldContainer graph to set state to IDLE (the precondition for showing the HUD).
2. Use TryShowPanelByName reflection helper (existing harness pattern at DclPlaytestHarness.cs:889-925) to invoke NearbyVoicePanelController.IssueCommand() (0-arg) → call mvcManager.ShowAsync<NearbyVoiceWidgetView, ControllerNoData>(command, CancellationToken.None) with automatic generic arg inference.
3. Settle: yield for 2-4 frames to allow the popup animation (ViewBaseWithAnimationElement base clas
- dataNeeds: none

## Capture Driver: Connection Status Panel (HUD / Realm-Comms Health)
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: Explorer/Assets/DCL/UI/ConnectionStatusPanel/ConnectionStatusPanelController.cs:8 (controller) + Explorer/Assets/DCL/UI/Co
- trigger: Open connection-status panel via chat /debug command -> ConnectionStatusPanelPlugin enables panel visibility -> ConnectionPanelView.Toggle() shows the realm/LiveKit health indicators (Scene/Scene Room
- driver: 
**Core Architecture:**
- **Controller**: DCL.UI.ConnectionStatusPanel.ConnectionStatusPanelController (file:line Explorer/Assets/DCL/UI/ConnectionStatusPanel/ConnectionStatusPanelController.cs:8)
- **View**: DCL.UI.ConnectionStatusPanel.ConnectionPanelView (file:line Explorer/Assets/DCL/UI/ConnectionStatusPanel/ConnectionPanelView.cs:7)
- **Plugin**: DCL.PluginSystem.Global.ConnectionStatusPanelPlugin (file:line Explorer/Assets/DCL/PluginSystem/Global/ConnectionStatusPanelPlugi
- dataNeeds: none

## profilewidget
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Click the sidebar profile avatar widget to open the Profile menu popup showing wallet address, username, and profile picture
- driver: 1. Via reflection: Resolve mvcManager from dynamicContainer.MvcManager (existing harness pattern, line ~509 in DclPlaytestHarness.cs).

2. Resolve ProfileMenuController type: FindType("DCL.UI.Profiles.ProfileMenuController") at Explorer/Assets/DCL/UI/Profiles/ProfileMenuController.cs:18.

3. Call ProfileMenuController.IssueCommand() (static, 0-arg, line 18-15 in ControllerBase.cs) to create ShowCommand<ProfileMenuView, ControllerNoData>.

4. Call mvcManager.ShowAsync<ProfileMenuView, ControllerNoData>(command, CancellationToken.None) via reflection (exact
- dataNeeds: none — the profile data (wallet address, username, profile picture) is sourced from identityCache.Identity (cached Evaristo login, populated by the ha

## explore
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: DCL.ExplorePanel.ExplorePanelController + DCL.ExplorePanel.ExplorePanelView; Explorer/Assets/DCL/ExplorePanel/ExplorePanelController.cs:35
- trigger: Press MainMenu shortcut key (Esc) to open Explore panel shell at default section (Events if Discover enabled, else Communities if allowed, else Navmap)
- driver: 1. Obtain MVCManager from MainSceneLoader.bootstrapServices via reflection.
2. Call ExplorePanelController.IssueCommand(default(ExplorePanelParameter)) to create ShowCommand<ExplorePanelView, ExplorePanelParameter>.
3. Invoke MVCManager.ShowAsync<ExplorePanelView, ExplorePanelParameter>(command, CancellationToken.None).
4. Poll controller.State until it is not ViewHiding (fade-in animation complete, ~0.2s).
5. Wait for SetupExploreSectionsAsync to complete (tab initialization, <1s typical).
6. Total settle: ~1.5s; verify controller.State is ViewFocused or ViewShowing.
7. Call ScreenCapture.Cap
- dataNeeds: none

## Place Detail Panel Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Click a place card in Explore > Places section to open its detail popup
- driver: 
1. LOCATE THE MVC MANAGER: Reflect into MainSceneLoader.dynamicWorldContainer.MvcManager (IMVCManager), per existing harness pattern at DclPlaytestHarness.cs:407-410.

2. OPEN EXPLORE > PLACES SECTION: Call TryOpenExplorePanel(mvcManager, "Places", null) as per DclPlaytestHarness.cs:806-834. This uses reflection to invoke ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Places)) -> mvcManager.ShowAsync<TView,TInput>(command, CancellationToken.None).

3. ENUMERATE/FETCH PLACES: Once Places section is loaded, the PlacesResultsController (cached inside PlacesControll
- dataNeeds: None; the Places API is live and public. Test account 'Evaristo' (cached identity loaded by harness at boot) can fetch any place. No authentication, O

## Event Detail Panel Capture Driver Design
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: User clicks an event card from the Explore Events section (calendar or daily view) to open the event detail popup
- driver: 1. Fetch event data via HttpEventsApiService.GetEventsByDateRangeAsync or GetHighlightedEventsAsync to populate the harness cache.
2. Construct EventDetailPanelParameter with: (a) an EventDTO fetched from the API, (b) optional PlacesData.PlaceInfo from placesAPIService.GetDestinationsByIdsAsync (lookup by event.place_id), and (c) null for the summoner EventCardView (not needed for reflection-based show).
3. Call EventDetailPanelController.IssueCommand(parameter) to construct a ShowCommand<EventDetailPanelView, EventDetailPanelParameter>.
4. Call mvcManager.ShowAsync<EventDetailPanelView, Event
- dataNeeds: One EventDTO from GetEventsByDateRangeAsync (passing a date range covering today and the next few days, with_connected_users=true to match live event 

## mapfilters - Map Filters/Layers Screen Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open the Explore panel to Navmap section, then toggle the filter panel button to show/hide the map filters (layers) UI overlay with toggles for Minigames, Live Events, Favorites, POIs, People, Satelli
- driver: 1. Call ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Navmap)) via reflection (ControllerBase<TView,TInputData>.IssueCommand static method at Explorer/Assets/DCL/Infrastructure/MVC/ControllerBase.cs:42) to create a ShowCommand<ExplorePanelView, ExplorePanelParameter>.

2. Invoke IMVCManager.ShowAsync<ExplorePanelView, ExplorePanelParameter>(command, cancellationToken) via reflection (see existing TryShowPanelByName pattern at Explorer/Assets/DCL/Harness/Editor/DclPlaytestHarness.cs:889
- dataNeeds: none

## Map Search Results Screen - Navigation Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open the Explore panel to Navmap section, then invoke the map search via NavmapBus.SearchForPlaceAsync with user-provided search text
- driver: 1. **Reach the Navmap section** via the existing harness helper pattern (already supports via TryOpenExplorePanel):
   - Get mvcManager from dynamicContainer via reflection
   - Call TryOpenExplorePanel(mvcManager, "Navmap", null, out error) using the existing helper at line 806-834 of DclPlaytestHarness.cs
   - Close any prior panels via CloseOpenPanels(mvcManager) at line 931-943
   - Settle 8-12 frames as per existing panel-open pattern (line 522)

2. **Access the NavmapBus** to trigger the search:
   - Get dynamicContainer from mainSceneLoader via reflection: GetPrivateField(mainSceneLoade
- dataNeeds: A valid search query string (e.g. "Origin", "Plaza", "Museum"). The test account 'Evaristo' has no preconditions; the Places API will return public de

## Backpack Emotes Tab Capture Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open Backpack panel with Emotes tab selected via reflection-driven MVC call
- driver: **Reflection Driver Recipe:**

1. Locate MVC Manager: `dynamicWorldContainer.MvcManager` from MainSceneLoader (via reflection as shown in DclPlaytestHarness:509)

2. Construct ExplorePanelParameter with Emotes tab:
   - Type: `DCL.ExplorePanel.ExplorePanelParameter` (struct in Explorer/Assets/DCL/ExplorePanel/ExplorePanelController.cs:475-493)
   - Constructor call: `new ExplorePanelParameter(ExploreSections.Backpack, BackpackSections.Emotes)`
   - Parameters:
     * `section` = `ExploreSections.Backpack` (enum DCL.UI.ExploreSections:3)
     * `backpackSe
- dataNeeds: none - inventory/emotes are pre-loaded with cached identity profile during TTI

## Backpack -> Outfits Tab Screen Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Backpack (Explore panel) -> Avatar section -> click Outfits tab to display the saved outfit slots grid
- driver: 1. SHOW EXPLORE PANEL (Backpack section) via reflection:
   Type ExplorePanelController = FindType("DCL.ExplorePanel.ExplorePanelController")
   Type ExplorePanelParameter = FindType("DCL.ExplorePanel.ExplorePanelParameter")
   Type ExploreSections = FindType("DCL.UI.ExploreSections")
   object backpackSection = Enum.Parse(ExploreSections, "Backpack")
   ConstructorInfo ctor = ExplorePanelParameter.GetConstructors()[0]
   object[] ctorArgs = new object[3] { backpackSection, null, null }
   object param = ctor.Invoke(ctorArgs)  // ExplorePanelParameter(Backpack)
   MethodInfo issueCmd = Explore
- dataNeeds: None required at runtime. The test account "Evaristo" is auto-logged in via the harness debug-skip. OutfitsPresenter will call LoadOutfitsCommand.Exec

## iteminfo - Wearable Detail Panel
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Select a wearable in Backpack > Avatar section; the ItemInfoPanel (right side) populates with wearable metadata (name, rarity, description, thumbnail)
- driver: 1. PREREQUISITE - Source a wearable URN from player's backpack via IWearablesProvider.GetTrimmedByParamsAsync(Params(1, 1), All, ct) and extract first wearable.GetUrn(); or use IEquippedWearables.Wearable(category).GetUrn() for speed.

2. TRIGGER - Resolve IBackpackCommandBus singleton, construct BackpackSelectWearableCommand(wearableUrn, null), and invoke commandBus.SendCommand(command).

3. SETTLE - Poll BackpackInfoPanelView.FullPanel.activeInHierarchy == true AND BackpackInfoPanelView.Name.text != empty AND BackpackInfoPanelView.WearableThumbnail.sprite != null. Or subscribe to IBackpackEv
- dataNeeds: none - test account Evaristo has default wearables in backpack post-spawn (verified via harness Genesis Plaza entry flow)

## friendactions - Unfriend/Block Confirm Popup Screen Capture Driver
- feasible: needs-data | ctx: in-world-session | conf: medium
- controller/view: DCL.Friends.UI.UnfriendConfirmationPopupController (and view UnfriendConfirmationPopupView) at Explorer/Assets/DCL/Friends/UI/UnfriendConfirmationPopupController
- trigger: The unfriend confirmation popup is shown when RemoveFriend button is clicked in PassportController, after opening a friend's passport via PassportController.IssueCommand with a friend's UserId
- driver: 1. LOCATE SERVICES: Via reflection, from MainSceneLoader (located via FindMainSceneLoader), access the DynamicWorldContainer and extract MvcManager (GetPublicProperty(dynamicContainer, "MvcManager")) and the plugin's FriendsService (via ObjectProxy lookup or GetFriendsService from the containers).

2. GET A FRIEND: Call IFriendsService.GetFriendsAsync(pageNum: 0, pageSize: 50, ct) asynchronously via AwaitUniTask reflection (pattern: invoke the method, poll IsCompleted via GetPublicProperty on the returned UniTask, extract Result when done). Extract the first friend from PaginatedFriendsResult.
- dataNeeds: A friend account in the test account "Evaristo"'s friends list. The test account likely has no friends; the driver requires either: (a) pre-establishi

## Community Card Driver - Social/Communities Section
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open the Explore panel to Communities section, retrieve a community ID from GetUserCommunitiesAsync API, then programmatically invoke CommunityCardController to show the detail card.
- driver: 1. **Navigate to Communities section**: Use reflection to call ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Communities)) and invoke MVCManager.ShowAsync() with CancellationToken.None. This opens the CommunitiesBrowserController section. (See DclPlaytestHarness.cs:806-834, TryOpenExplorePanel method)

2. **Fetch a community ID**: Via reflection, instantiate CommunitiesDataProvider and call GetUserCommunitiesAsync(name="", onlyMemberOf=false, pageNumber=1, elementsPerPage=10, CancellationToken.None). Parse the response to extract a valid community ID from respon
- dataNeeds: A valid community ID (sourced from the live Communities API at runtime; the test account 'Evaristo' will have at least one joined community or can fet

## Create-community wizard capture driver (communitycreate)
- feasible: needs-data | ctx: in-world-session | conf: high
- controller/view: DCL.Communities.CommunityCreation.CommunityCreationEditionController @ Explorer/Assets/DCL/Communities/CommunityCreation/CommunityCreationEditionController.cs:30
- trigger: Click "Create Community" button in Communities section (Explore panel) -> CommunityCreationEditionView (wizard modal)
- driver: 
## Step 1: Navigate to Communities section
Call via reflection: `ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Communities))` -> `mvcManager.ShowAsync<ExplorePanelView, ExplorePanelParameter>(command, CancellationToken.None)`
Settle 3-4 frames for panel to render.

## Step 2: Verify Communities section is shown
Read `mvcManager.Controllers[IController<ExplorePanelView, ExplorePanelParameter>].State` — must be "ViewShowing" or "ViewShown".

## Step 3: Invoke the Create Community flow
Retrieve from dynamicContainer -> CommunitiesBrowserController (held privately 
- dataNeeds: Test account 'Evaristo' must own >=1 NFT name in their web3 wallet (or the splash screen "Get Name" panel shows instead of the creation form). Check: 

## communitymembers: Community Members/Requests/Banned Screen Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open a community card via CommunityCardController.IssueCommand, then switch to the MEMBERS section tab and programmatically switch to Requests/Banned via MembersListView section callbacks.
- driver: 1. Reflection: Get mvcManager from scene (via MainSceneLoader reflection as documented in DclPlaytestHarness.cs line 507+).

2. Open community card:
   - Get CommunityCardController type (fully-qualified: DCL.Communities.CommunitiesCard.CommunityCardController)
   - Call static IssueCommand(new CommunityCardParameter(communityId))
   - Invoke mvcManager.ShowAsync<CommunityCardView, CommunityCardParameter>(command, ct)
   - Wait for panel to settle via awaitability check (see DclPlaytestHarness.cs line 946+)

3. Get the MembersListView from CommunityCardView (field: public MembersListView Membe
- dataNeeds: A communityId string (UUID format, e.g., from ScenesCache/RealmData or test data). The harness can query communitiesDataProvider to list joined commun

## Community Places/Events Screen Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: DCL.Communities.CommunitiesCard.CommunityCardController + DCL.Communities.CommunitiesCard.CommunityCardView @ Explorer/Assets/DCL/Communities/CommunitiesCard/Com
- trigger: Navigate Explore panel → Communities section → open a community card → switch to Places/Events tab via ToggleSection(PLACES)
- driver: 1. LOCATE RUNTIME SERVICES via reflection on MainSceneLoader (existing harness pattern):
   - Find MainSceneLoader via reflection: FindType("Global.Dynamic.MainSceneLoader") then FindAnyObjectByType
   - Get dynamicContainer via GetPrivateField(mainSceneLoader, "dynamicContainer")
   - Get mvcManager via GetPublicProperty(dynamicContainer, "MvcManager")

2. FETCH COMMUNITY DATA (required to get a valid communityId + places):
   - Obtain CommunitiesDataProvider via reflection on dynamicContainer or MainSceneLoader
   - Call CommunitiesDataProvider.GetAllCommunitiesAsync(CancellationToken ct) to
- dataNeeds: A community with at least one place/event. The test user 'Evaristo' must have access (joined or public community). CommunitiesFeatureAccess.Instance m

## Voice-stream join panel capture driver (communitystream)
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: DCL.Communities.CommunitiesCard.CommunityCardController + DCL.Communities.CommunitiesCard.CommunityCardVoiceChatView / Explorer/Assets/DCL/Communities/Communitie
- trigger: Show community card for a community with active voice stream, the JoinStreamButton in the LiveStreamPanel section is displayed when voiceChatStatus.isActive=true and user is not a member
- driver: 
1. Get a community with active voice stream from CommunitiesBrowserController or use a known community ID with stream status.

2. Reflection call chain:
   a. CommunityCardController.IssueCommand(new CommunityCardParameter(communityId, spriteCache))
      - File: Explorer/Assets/DCL/Communities/CommunitiesCard/CommunityCardController.cs:50 (line 50 has class definition)
      - Parameter construction: Pass the communityId string and an ISpriteCache instance (can reuse harness cache or pass null for default)
   
   b. MVCManager.ShowAsync<CommunityCardVie
- dataNeeds: A community with an active voice stream (voiceChatStatus.isActive=true) where the test account is NOT a moderator or owner. The communitystream label 

## passport-card-social-driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Click a friend's name/avatar in the Friends panel to view their Passport card (Profile overview section)
- driver: REFLECTION-ONLY DRIVER (no input simulation needed):

1. Obtain the IPassportBridge from dependency injection or MVCManager:
   - IPassportBridge is registered in the DI container during bootstrap
   - Currently exposed as MVCPassportBridge (DCL.Friends.Passport namespace)
   - Alternative: Use MVCManager directly with PassportController.IssueCommand

2. Construct PassportParams with the target friend's userId:
   - Type: DCL.Passport.PassportParams (file: Explorer/Assets/DCL/Passport/Bridge/PassportParams.cs:1-16)
   - Constructor: new PassportParams(str
- dataNeeds: A friend's userId (Web3Address/wallet string). Test account "Evaristo" must have at least one friend in the friends list. If not, the test requires:
1

## Passport Photos Tab Driver Recipe (passportphotos)
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Passport.Photos tab is opened via PassportController.IssueCommand, then OpenPhotosSection() is called internally when currentSection changes to PHOTOS
- driver: 1. ACQUIRE DATA: Call selfProfile.ProfileAsync() to get the test account's own UserId. For capturing a friend's Passport photos, use any friend's userId from the FriendsService.GetFriendsListAsync(). Note: Evaristo (the harness test account) must have at least one photo in their wallet gallery (CameraReelStorageService.UnsignedGetUserGalleryStorageInfoAsync(userId, ct)).

2. REFLECTION CHAIN to show Passport -> Photos tab:
   a) Get mvcManager (via MainSceneLoader.dynamicWorldContainer.MvcManager)
   b) Find PassportController type: Type.GetType("DCL.Passport.PassportController, DCL.Passport.a
- dataNeeds: userId: Must be a web3 address string of a user with at least one photo in their CameraReel wallet gallery. Evaristo (cached test identity) must have 

## Badge Detail Capture Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Badge detail subview is shown by opening Passport with PassportParams(userId, badgeIdSelected) and navigating to BADGES section, where BadgesDetails_PassportModuleController.SetBadgeByDefault(badgeId)
- driver: 1. Get mvcManager via reflection: FindMainSceneLoader() -> GetPrivateField("dynamicWorldContainer") -> GetPublicProperty("MvcManager").

2. Fetch badgesAPIClient from the running PassportController (cached in field) OR fetch badges directly: FindControllerByTypeName(mvcManager, "BadgesAPIClient") to load badge list.

3. Construct PassportParams with userId="0x..." (use selfProfile.ProfileAsync() to get the test account "Evaristo"), badgeIdSelected=first badge's data.id (from FetchBadgesAsync response), isOwnProfile=true.

4. Find PassportController type via reflection; call IssueCommand(Passpo
- dataNeeds: Evaristo (test account identity, already cached in editor) with at least one achieved badge. The harness already has the identity and can call FetchBa

## creditsunlocked
- feasible: needs-data | ctx: in-world-session | conf: medium
- controller/view: 
- trigger: Click Marketplace Credits button in sidebar → auto-navigate to Goals of the Week section → solve CAPTCHA slider → claim credits → display Credits Unlocked popup
- driver: 1. **Precondition checks** (via reflection):
   - Verify `ISelfProfile.ProfileAsync()` returns a user with `UserId`
   - Call `MarketplaceCreditsAPIClient.GetProgramProgressAsync(userId, ct)` to fetch `CreditsProgramProgressResponse`
   - Verify that `creditsProgramProgress.SomethingToClaim()` is TRUE and `creditsProgramProgress.credits.isBlockedForClaiming` is FALSE
   - Verify `creditsProgramProgress.IsUserEmailVerified()` is TRUE (to reach Goals of the Week, not Verify Email)

2. **Navigate to Marketplace Credits panel** (via MVC reflection):
   - Call `MarketplaceCreditsMenuController.Issu
- dataNeeds: The Evaristo test account MUST have: (1) completed email verification in the Marketplace Credits program, (2) unclaimed weekly goals with claimable cr

## donations - Send a Tip Dialog Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: User selects a Place in Explore/Places section and clicks "Donate" button on the Place Info panel to open the Send a Tip dialog
- driver: 1. NAVIGATE TO PLACES SECTION (via reflection):
   - Locate MVCManager instance: FindControllerByTypeName(mvcManager, "ExplorePanelController")
   - Call ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Places))
   - Invoke mvcManager.ShowAsync<TView,TInput>(command, CancellationToken.None)
   - Poll MVCManager.Controllers[typeof(IController<ExplorePanelView, ExplorePanelParameter>)].State until "ViewFocused" or "ViewShowing" (settle ~3-5 frames)

2. FETCH A PLACE WITH CREATOR (via Places API):
   - From ExplorePanelController, retrieve: PlacesController -> PlacesR
- dataNeeds: A Place entity in the live Places API with a non-null creator_address field. The test account "Evaristo" needs basic mana (MANA token) to initiate the

## Gifting Flow Driver Recipe for DclPlaytestHarness
- feasible: needs-data | ctx: in-world-session | conf: medium
- controller/view: 
- trigger: Open Gifting UI (item selection) by calling GiftSelectionController.IssueCommand with recipient user ID and name; then select a wearable and proceed to the GiftTransferController transfer/wallet-confi
- driver: 
STEP 1: Obtain a Friend User ID and Display Name
- Query the FriendsService (injected into MainSceneLoader's DI container) or call the Friends API
- Evaristo test account: likely has no friends or requests (ENV-LIMITED per harness line 562)
- Alternative: Use a hard-coded known test friend user ID (if one exists in the test environment)
- Fallback: Use a mock user ID like "0x123456789abcdef" if Evaristo has no friends list

STEP 2: Obtain Own User ID
- Reflect to get selfProfile from DI container, call ProfileAsync(ct) to get own Profile
- Extract Profile.UserId as the gifter's address

STEP 
- dataNeeds: Friend user ID + display name (test account "Evaristo" likely has none; requires either: social-service integration, mock friend creation, or hard-cod

## in-world camera HUD screen capture driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Player enters in-world camera mode via ECS ToggleInWorldCameraRequest; HUD panel renders automatically via InWorldCameraController.Show()
- driver: 1. Reach time-to-interactive (LoadingStage.Completed) at Genesis Plaza (existing harness flow).
2. Locate camera entity: camera = World.CacheCamera() (see ToggleInWorldCameraActivitySystem.Initialize() line 71).
3. Add ToggleInWorldCameraRequest component to enable camera mode:
   - world.Add(camera, new ToggleInWorldCameraRequest { IsEnable = true, Source = "Harness" });
4. Yield ~5 frames to allow ToggleInWorldCameraActivitySystem.Update() (line 94-96) to fire and call EnableCamera().
5. Inside EnableCamera (line 155-189), the system calls hudController.Show() (line 166), which invokes:
   -
- dataNeeds: none

## Photo Detail Screen Driver Recipe
- feasible: needs-data | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open a photo detail panel by showing PhotoDetailController with a reel list and current index from the CameraReel Explore section
- driver: 1. PREREQUISITE: Ensure logged-in to Evaristo account with cached identity (standard harness bootstrap).

2. OPEN CAMERA REEL SECTION: Call ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.CameraReel)) via MVCManager.ShowAsync<ExplorePanelView, ExplorePanelParameter>. This opens the Explore panel showing the CameraReel section (the gallery of the user's screenshots).

3. FETCH REELS DATA: Reflect into CameraReelController.CameraReelGalleryController and call its PagedCameraReelManager to get the list of CameraReelResponseCompact objects. Alternatively, call ICamera
- dataNeeds: CameraReelResponseCompact list: The test account 'Evaristo' must have at least ONE photo/screenshot saved in their camera reel gallery. If the account

## emotewheel
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Press B key / Emote wheel shortcut to open the radial emote selector overlay (10 slots, plays equipped emotes)
- driver: 
1. **Get the MVCManager**: Retrieve from dynamicContainer via reflection: `GetPublicProperty(dynamicContainer, "MvcManager")`

2. **Issue the command** via reflection:
   - Find type: `DCL.EmotesWheel.EmotesWheelController` (Explorer/Assets/DCL/EmotesWheel/EmotesWheelController.cs:24)
   - Invoke static method: `EmotesWheelController.IssueCommand()` (0-arg, returns `ShowCommand<EmotesWheelView, ControllerNoData>`)
   - See ControllerBase.cs:14 for the definition of the 0-arg path

3. **Show the panel**:
   - Call `mvcManager.ShowAsync<EmotesWheelView, Co
- dataNeeds: none

## Emoji Panel Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Open emoji picker overlay via chat input - click emoji button in focused chat input field
- driver: STEP 1: Show Chat (Prerequisite)
  Call: ChatMainSharedAreaController.IssueCommand() static method (no args)
  Type: Explorer/Assets/DCL/Chat/SharedArea/ChatMainSharedAreaController.cs:12
  Invoke via MVCManager.ShowAsync<ChatMainSharedAreaView, ChatMainSharedAreaController.NoData>
  Wait ~5 frames for chat panel to render and settle

STEP 2: Focus Chat Input (Prerequisite)
  Reflection: Get ChatInputPresenter instance from the loaded chat services
  Invoke: ChatInputPresenter.ShowFocusedAsync() (no args, returns UniTask)
  File: Explorer/
- dataNeeds: none - uses cached "Evaristo" identity already logged in at time-to-interactive; no friend/community/place/NFT/wallet required; emoji panel is UI-only

## Skybox Flyout UI Capture Driver Design
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Sidebar SKY button click opens the Skybox flyout panel (time-of-day controls) via MVC reflection
- driver: 1. Call mvcManager.ShowAsync(SkyboxMenuController.IssueCommand(), cancellationToken) via reflection, using TryShowPanelByName helper pattern from DclPlaytestHarness.cs:889-925.
2. Type resolution: Find type "DCL.UI.Skybox.SkyboxMenuController" via reflection.
3. IssueCommand lookup: SkyboxMenuController is ControllerBase<SkyboxMenuView> (no input data), so call static SkyboxMenuController.IssueCommand() with zero args (ControllerBase.cs:14 shows the pattern).
4. Construct ShowCommand<SkyboxMenuView, ControllerNoData> from the IssueCommand return value.
5. Invoke mvcManager.ShowAsync(command, C
- dataNeeds: none

## Teleport Prompt UI Screen Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Teleport prompt popup opens when a scene calls UserActions.RequestTeleport(coords) or when RestrictedActionsAPI.TryTeleportTo(coords) is invoked from in-world (e.g., clicking a teleport hyperlink in a
- driver: ENTRY: Invoke via reflection from the harness RunSessionCoroutine or a new standalone routine.

1. OBTAIN TARGET COORDINATES: Use the existing TELEPORT_TARGETS array (line 63-72 in DclPlaytestHarness.cs) or pick a hardcoded parcel like Vector2Int(0,0) (Genesis Plaza spawn) or Vector2Int(74,-9) (populated GP parcel).

2. FIND MVC MANAGER: Existing harness code (line 509) shows the pattern:
   object dynamicContainer = GetPrivateField(mainSceneLoader, "dynamicWorldContainer");
   object mvcManager = GetPublicProperty(dynamicContainer, "MvcManager");

3. INVOKE TELEPORT COMMAND via reflection usi
- dataNeeds: none - the harness test account 'Evaristo' has cached identity (skips auth via debugSettings.showAuthentication=false per line 223) and Genesis Plaza 

## NFT Prompt Screen Driver Recipe
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: In-world NFT interaction via scene's restricted-actions API or direct reflection of NftPromptController.IssueCommand with URN coordinates
- driver: 
## Exact Controller/View/Popup Classes
- **Controller (fully-qualified + file:line)**: `DCL.NftPrompt.NftPromptController : ControllerBase<NftPromptView, NftPromptController.Params>` at `Explorer/Assets/DCL/NftPrompt/NftPromptController.cs:16`
- **View (fully-qualified + file:line)**: `DCL.NftPrompt.NftPromptView : ViewBase, IView` at `Explorer/Assets/DCL/NftPrompt/NftPromptView.cs:9`
- **Params struct (fully-qualified + file:line)**: `DCL.NftPrompt.NftPromptController+Params` at `Explorer/
- dataNeeds: 
Hardcoded test NFT coordinates (chain/contract/tokenId) available from unit test. No real wallet/signature/live stream data required. OpenSeaAPIClien

## Decentraland Reward Panel Capture Driver
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Show Reward panel by injecting a RewardInProgressNotification via NotificationsBusController.Instance.AddNotification
- driver: 1. Locate NotificationsBusController via reflection: `NotificationsBusController.Instance` (singleton, see line 8 in NotificationsBusController.cs).

2. Construct a RewardInProgressNotification instance programmatically:
   - Type: `DCL.NotificationsBus.NotificationTypes.RewardInProgressNotification`
   - Set base properties (inherited from NotificationBase): Id, Type, Address, Timestamp, Read
   - Set Metadata (IncomingRewardNotificationMetadata struct at line 22 in RewardInProgressNotification.cs):
     - tokenName: "Test Reward" (or any wearable name)
     - tokenImage: URL to a test image 
- dataNeeds: none - the test account 'Evaristo' does not need a real reward from the backend; the notification is constructed purely in-memory and injected into th

## Private-worlds entry popup (password-required mode) capture driver
- feasible: needs-data | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Attempt to teleport to a password-protected private world via RealmNavigator.TryChangeRealmAsync(isWorld:true), which triggers PrivateWorldAccessHandler.CheckAccessAsync, triggering PrivateWorldPopupC
- driver: 
PREREQUISITE DATA: A password-protected private world domain/URL accessible at runtime. The harness can discover live worlds via Places API (already in the harness at SwitchToWorldAndShoot, line 1199-1250). Worlds with AccessType=SharedSecret require a password; these become PasswordRequired mode in the popup.

SETUP STEP 1 - Locate/Verify a Password-Protected World:
- Scan the Places API (existing harness pattern at line 1017-1048) with SearchDestinationsAsync(pageNumber=0, pageSize=50, ct, ...) and filter for world_name != null.
- For each candidate world, invoke IWorldPermissionsService.Ge
- dataNeeds: 
A password-protected private world (AccessType=SharedSecret) reachable at runtime from the test network.
Precondition: "Evaristo" (the auto-cached te

## smartwearables - Smart-wearable PEX popup authorization flow
- feasible: yes-automated | ctx: in-world-session | conf: high
- controller/view: 
- trigger: Activate a smart wearable from backpack grid → popup requests permission for PEX scene permissions
- driver: REFLECTION-DRIVEN capture via MVC:

1. OBTAIN WEARABLE: Query the running wearableStorage via reflection to find any smart wearable in Evaristo's backpack. SmartWearableCache.IsSmartAsync(wearable, ct) determines if a wearable is smart.
   - Reflect on MainSceneLoader.dynamicWorldContainer → MvcManager → MVCManager.GetPublicProperty("WearableStorage")
   - Iterate wearableStorage.GetAll() or query by URN. Target wearable must pass smartWearableCache.RequiresAuthorizationAsync(wearable, ct) == true (has required permissions).
   - If none found, create a mock IWearable (Wearable.NewEmpty()) wit
- dataNeeds: Test account 'Evaristo' must own at least one smart wearable with required permissions (Web3 API, OPEN_EXTERNAL_LINK, WEBSOCKET, or FETCH API). If the

