using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;

namespace ScholarFlow.Core.Tests;

public sealed class MultiBasinSyncCoordinatorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"sf-coord-test-{Guid.NewGuid():N}"
    );

    public MultiBasinSyncCoordinatorTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task BasinService_RoundTripsPerBasinSyncConfiguration()
    {
        var basin = new BasinService();
        var root = Path.Combine(_tempRoot, "basin-config");
        await basin.InitializeOrOpenAsync(root, "College");

        var updated = new BasinConfig
        {
            SchemaVersion = 2,
            Name = "College",
            ProviderId = SyncProviderIds.StiElms,
            AccountKey = "student-1",
            BackgroundSyncEnabled = true,
            FastPollIntervalMinutes = 10,
            Terms = ["Prelims", "Midterms", "Finals"],
            ExcludedCourseIds = ["999001"],
        };
        await basin.SaveBasinConfigAsync(root, updated);

        var reloaded = await basin.InitializeOrOpenAsync(root);
        Assert.Equal(2, reloaded.SchemaVersion);
        Assert.Equal(SyncProviderIds.StiElms, reloaded.ProviderId);
        Assert.Equal("student-1", reloaded.AccountKey);
        Assert.True(reloaded.BackgroundSyncEnabled);
        Assert.Equal(10, reloaded.FastPollIntervalMinutes);
        Assert.Equal(["Prelims", "Midterms", "Finals"], reloaded.Terms);
        Assert.Equal(["999001"], reloaded.ExcludedCourseIds);
    }

    [Fact]
    public void Registry_ResolvesLocalProviderAndRegisteredProviders()
    {
        var mock = new FakeSyncProvider("mock-lms", enforceSingleSession: true);
        var registry = new BasinSyncProviderRegistry([mock]);

        Assert.Same(LocalKnowledgeBasinSyncProvider.Instance, registry.Resolve(SyncProviderIds.None));
        Assert.Same(LocalKnowledgeBasinSyncProvider.Instance, registry.Resolve(null));
        Assert.Same(LocalKnowledgeBasinSyncProvider.Instance, registry.Resolve("unknown-provider"));
        Assert.Same(mock, registry.Resolve("mock-lms"));
        Assert.False(registry.Resolve(SyncProviderIds.None).Capabilities.SupportsSync);
        Assert.True(registry.Resolve("mock-lms").Capabilities.EnforcesSingleConcurrentSession);
    }

    [Fact]
    public async Task Coordinator_PauseShieldAndConcurrencyGateIsolateByProviderAndAccountKey()
    {
        var basin = new BasinService();
        var rootA1 = Path.Combine(_tempRoot, "elms-a1");
        var rootA2 = Path.Combine(_tempRoot, "elms-a2");
        var rootB = Path.Combine(_tempRoot, "elms-b");
        var rootLocal = Path.Combine(_tempRoot, "knowledge-hub");

        await basin.InitializeOrOpenAsync(rootA1, "ELMS A1");
        await basin.InitializeOrOpenAsync(rootA2, "ELMS A2");
        await basin.InitializeOrOpenAsync(rootB, "ELMS B");
        await basin.InitializeOrOpenAsync(rootLocal, "Knowledge Hub");

        var cfgA1 = new BasinConfig
        {
            SchemaVersion = 2,
            Name = "ELMS A1",
            ProviderId = "mock-lms",
            AccountKey = "acct-a",
            BackgroundSyncEnabled = false,
        };
        var cfgA2 = new BasinConfig
        {
            SchemaVersion = 2,
            Name = "ELMS A2",
            ProviderId = "mock-lms",
            AccountKey = "acct-a",
            BackgroundSyncEnabled = false,
        };
        var cfgB = new BasinConfig
        {
            SchemaVersion = 2,
            Name = "ELMS B",
            ProviderId = "mock-lms",
            AccountKey = "acct-b",
            BackgroundSyncEnabled = false,
        };
        var cfgLocal = new BasinConfig
        {
            SchemaVersion = 2,
            Name = "Knowledge Hub",
            ProviderId = SyncProviderIds.None,
            BackgroundSyncEnabled = false,
        };

        var fakeProvider = new FakeSyncProvider("mock-lms", enforceSingleSession: true);
        await using var coordinator = new MultiBasinSyncCoordinator(
            new BasinSyncProviderRegistry([fakeProvider]),
            basin
        );

        await coordinator.RegisterOrUpdateBasinAsync(rootA1, cfgA1, startLoop: false);
        await coordinator.RegisterOrUpdateBasinAsync(rootA2, cfgA2, startLoop: false);
        await coordinator.RegisterOrUpdateBasinAsync(rootB, cfgB, startLoop: false);
        await coordinator.RegisterOrUpdateBasinAsync(rootLocal, cfgLocal, startLoop: false);

        // Pausing acct-a pauses both A1 and A2, leaving acct-b unpaused
        var keyA = fakeProvider.GetConcurrencyKey(cfgA1);
        var keyB = fakeProvider.GetConcurrencyKey(cfgB);
        coordinator.PauseProvider(keyA);

        Assert.True(coordinator.IsProviderPaused(keyA));
        Assert.False(coordinator.IsProviderPaused(keyB));
        Assert.True(coordinator.IsAnySingleSessionProviderPaused());

        // Concurrent triggers on rootA1 & rootA2 serialize through provider lock
        coordinator.ResumeAllSingleSessionProviders();
        Assert.False(coordinator.IsAnySingleSessionProviderPaused());

        fakeProvider.ArtificialDelay = TimeSpan.FromMilliseconds(40);
        await Task.WhenAll(
            coordinator.TriggerFastPollAsync(rootA1),
            coordinator.TriggerFastPollAsync(rootA2)
        );
        Assert.Equal(1, fakeProvider.MaxObservedConcurrency);
        Assert.Equal(2, fakeProvider.FastPollCalls);

        // Local Knowledge Hub does not support sync
        var emitted = new List<BasinNotificationEvent>();
        coordinator.NotificationEmitted += (_, ev) => emitted.Add(ev);

        var localNotifs = await coordinator.TriggerFastPollAsync(rootLocal);
        Assert.Empty(localNotifs);

        fakeProvider.NextNotification = new AppNotification(
            NotificationKind.NewAssignment,
            "New Task",
            "Posted",
            null,
            null,
            DateTimeOffset.UtcNow
        );
        var a1Notifs = await coordinator.TriggerFastPollAsync(rootA1);
        Assert.Single(a1Notifs);
        Assert.Single(emitted);
        Assert.Equal(Path.GetFullPath(rootA1), emitted[0].BasinRoot);
    }

    [Fact]
    public async Task Coordinator_RegisterKnownBasinsHonorsLegacyElmsFallback()
    {
        var basin = new BasinService();
        var legacyRoot = Path.Combine(_tempRoot, "legacy");
        var cfg = await basin.InitializeOrOpenAsync(legacyRoot, "Legacy");
        cfg.SchemaVersion = 1;
        await basin.SaveBasinConfigAsync(legacyRoot, cfg);

        var fakeProvider = new FakeSyncProvider(SyncProviderIds.StiElms, enforceSingleSession: true);
        await using var coordinator = new MultiBasinSyncCoordinator(
            new BasinSyncProviderRegistry([fakeProvider]),
            basin
        );

        await coordinator.RegisterKnownBasinsAsync(
            [new KnownBasinEntry { Name = "Legacy", FilePath = legacyRoot }],
            globalElmsFallback: false
        );

        var state = coordinator.GetBasinState(legacyRoot);
        Assert.NotNull(state);
        Assert.Equal(SyncProviderIds.None, state.Config.ProviderId);
        Assert.False(state.Provider.Capabilities.SupportsSync);
    }

    [Fact]
    public async Task Coordinator_OpenExternalUrlWithPauseShieldPausesAndResumesSingleSessionProvider()
    {
        var basin = new BasinService();
        var root = Path.Combine(_tempRoot, "browser-basin");
        await basin.InitializeOrOpenAsync(root, "Browser Basin");
        var cfg = new BasinConfig
        {
            SchemaVersion = 2,
            Name = "Browser Basin",
            ProviderId = "mock-lms",
            AccountKey = "acct-browser",
            BackgroundSyncEnabled = false,
            ProviderSettings = new(StringComparer.OrdinalIgnoreCase)
            {
                ["baseUrl"] = "https://portal.example.edu",
            },
        };
        await basin.SaveBasinConfigAsync(root, cfg);

        Assert.Equal("https://portal.example.edu", cfg.GetProviderSetting("baseUrl"));

        var fakeProvider = new FakeSyncProvider("mock-lms", enforceSingleSession: true);
        await using var coordinator = new MultiBasinSyncCoordinator(
            new BasinSyncProviderRegistry([fakeProvider]),
            basin
        );
        await coordinator.RegisterOrUpdateBasinAsync(root, cfg, startLoop: false);

        var key = fakeProvider.GetConcurrencyKey(cfg);
        bool? pausedDuringSession = null;
        bool callbackInvoked = false;

        fakeProvider.OnOpenExternal = async (_, _, _, onClosed) =>
        {
            pausedDuringSession = coordinator.IsProviderPaused(key);
            if (onClosed is not null)
                await onClosed();
            return true;
        };

        var opened = await coordinator.OpenExternalUrlWithPauseShieldAsync(
            root,
            "/user_dashboard",
            () =>
            {
                callbackInvoked = true;
                return Task.CompletedTask;
            }
        );

        Assert.True(opened);
        Assert.True(pausedDuringSession);
        Assert.False(coordinator.IsProviderPaused(key));
        Assert.True(callbackInvoked);

        var authStatus = await coordinator.AuthenticateBasinAsync(root);
        Assert.True(authStatus.IsAuthenticated);
        await coordinator.DisconnectBasinAsync(root);
        Assert.Equal("Disconnected", coordinator.GetBasinState(root)?.LastStatus);
    }

    [Fact]
    public async Task BasinTaskSynchronizer_UpsertsProviderNeutralAssignmentsAndIdentifiesSyncableCourses()
    {
        var basin = new BasinService();
        var root = Path.Combine(_tempRoot, "sync-tasks");
        await basin.InitializeOrOpenAsync(root, "Canvas Basin");

        var localCourse = await basin.CreateLocalCourseAsync(root, "Self Study Rust", "rust");
        Assert.False(localCourse.IsSyncableCourse);

        var syncedCourses = await basin.UpsertCoursesAsync(
            root,
            [
                new DiscoveredCourse(
                    CourseId: "canvas-42",
                    Title: "Distributed Systems",
                    FolderName: "Distributed_Systems",
                    CourseCode: "CS402",
                    CourseUrl: "https://canvas.example.edu/courses/42"
                ),
            ]
        );
        var syncedCourse = Assert.Single(syncedCourses, c => c.CourseId == "canvas-42");
        Assert.True(syncedCourse.IsSyncableCourse);

        var pollState = new PollState();
        var notifs = new List<AppNotification>();
        var assignment = new SyncedAssignment(
            ExternalId: "a-100",
            CourseId: "canvas-42",
            Title: "Raft Consensus Lab",
            Url: "https://canvas.example.edu/courses/42/assignments/100",
            DueDate: DateTimeOffset.Now.AddDays(4),
            Status: SyncedAssignmentStatus.Pending,
            Score: null,
            TermHeader: "Unit 1",
            ParentModuleTitle: "01_Consensus",
            Kind: "Assignment",
            WeightPercent: 15,
            InstructionFiles: ["01_Consensus/raft_spec.pdf"],
            SubmissionFiles: []
        );

        var count = await BasinTaskSynchronizer.UpsertCourseAssignmentsAsync(
            root,
            syncedCourse,
            [assignment],
            pollState,
            notifs,
            CancellationToken.None
        );

        Assert.Equal(1, count);
        Assert.Contains(notifs, n => n.Kind == NotificationKind.NewAssignment);
        var loadedTasks = await basin.LoadAllTasksAsync(root);
        Assert.Single(loadedTasks);
        Assert.Equal("a-100", loadedTasks[0].LmsId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class FakeSyncProvider(string providerId, bool enforceSingleSession)
        : IBasinSyncProvider
    {
        private int _inFlight;
        private int _maxObserved;
        private int _fastPollCalls;

        public string ProviderId => providerId;
        public string DisplayName => "Mock LMS";

        public ProviderCapabilities Capabilities { get; } =
            new(
                SupportsSync: true,
                SupportsFastPoll: true,
                SupportsPerCourseSync: true,
                SupportsSingleItemRefresh: true,
                EnforcesSingleConcurrentSession: enforceSingleSession,
                DefaultTerms: ["Prelims", "Midterms", "Pre-Finals", "Finals"],
                SupportsInteractiveSessionBrowser: true
            );

        public Func<string, BasinConfig, string, Func<Task>?, Task<bool>>? OnOpenExternal { get; set; }
        public TimeSpan ArtificialDelay { get; set; } = TimeSpan.Zero;
        public AppNotification? NextNotification { get; set; }
        public int MaxObservedConcurrency => _maxObserved;
        public int FastPollCalls => _fastPollCalls;

        public string GetConcurrencyKey(BasinConfig config) =>
            $"{ProviderId}:{config.AccountKey ?? "default"}";

        public async Task<IReadOnlyList<AppNotification>> FastPollAsync(
            string basinRoot,
            bool allowInteractiveAuth = true,
            CancellationToken ct = default
        )
        {
            var current = Interlocked.Increment(ref _inFlight);
            UpdateMax(current);
            Interlocked.Increment(ref _fastPollCalls);
            try
            {
                if (ArtificialDelay > TimeSpan.Zero)
                    await Task.Delay(ArtificialDelay, ct);

                return NextNotification is not null ? [NextNotification] : [];
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<BasinSyncReport> SyncAllAsync(
            string basinRoot,
            bool forceRedownload = false,
            bool allowInteractiveAuth = true,
            IProgress<SyncProgress>? progress = null,
            CancellationToken ct = default
        ) => Task.FromResult(new BasinSyncReport(1, 0, 0, 0, 0, 0, [], []));

        public Task<BasinSyncReport> SyncCourseAsync(
            string basinRoot,
            string courseFolder,
            IProgress<SyncProgress>? progress = null,
            CancellationToken ct = default
        ) => Task.FromResult(new BasinSyncReport(1, 0, 0, 0, 0, 0, [], []));

        public Task<bool> SyncSingleTaskAsync(
            string basinRoot,
            TaskItem task,
            CancellationToken ct = default
        ) => Task.FromResult(true);

        public Task<bool> OpenExternalUrlAsync(
            string basinRoot,
            BasinConfig config,
            string urlOrPath,
            Func<Task>? onWindowClosed = null,
            CancellationToken ct = default
        ) =>
            OnOpenExternal is not null
                ? OnOpenExternal(basinRoot, config, urlOrPath, onWindowClosed)
                : Task.FromResult(false);

        private void UpdateMax(int current)
        {
            while (true)
            {
                var snapshot = _maxObserved;
                if (current <= snapshot)
                    return;
                if (Interlocked.CompareExchange(ref _maxObserved, current, snapshot) == snapshot)
                    return;
            }
        }
    }
}
