using System.Reflection;
using System.Text.RegularExpressions;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Source-level checks for invariants that no runtime test can protect.
/// </summary>
/// <remarks>
/// <para>
/// The plan requires these specifically because the coordination defects it went through were
/// all edit-time mistakes rather than logic errors: an <c>await</c> introduced inside a critical
/// section, a parameterless <c>WaitOne()</c> at a new call site, a second <c>UpdateWidget</c>
/// caller added for convenience. Each compiles, each passes every behavioural test on an
/// uncontended machine, and each fails rarely and remotely in production.
/// </para>
/// <para>
/// These read the product's own source. That is deliberate: the point is to fail the build when
/// someone writes the prohibited construction, not to observe its effects afterwards.
/// </para>
/// </remarks>
public sealed class CoordinationStaticAnalysisTests
{
    private static IEnumerable<string> CoreSourceFiles() => RepositorySources.CoreSourceFiles();

    private static string StripCommentsAndStrings(string source) =>
        RepositorySources.StripCommentsAndStrings(source);

    [Fact]
    public void No_call_site_uses_the_parameterless_WaitOne()
    {
        // An indefinite wait has no recovery path: a peer wedged inside a critical section would
        // hang the caller forever, which is precisely the failure the lock-free read design
        // exists to prevent.
        var prohibited = new Regex(@"\.WaitOne\s*\(\s*\)", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (string file in CoreSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            foreach (Match match in prohibited.Matches(code))
            {
                offenders.Add($"{Path.GetFileName(file)} at offset {match.Index}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Every mutex acquisition must pass a timeout. Parameterless WaitOne() found in: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void Named_event_signallers_share_one_operational_failure_policy()
    {
        var offenders = new List<string>();

        foreach (string file in CoreSourceFiles())
        {
            if (string.Equals(
                    Path.GetFileName(file),
                    "NamedEventSignal.cs",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (code.Contains("EventWaitHandle.OpenExisting", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Named event signalling must use NamedEventSignal so missing, inaccessible, and I/O "
                + "failure handling cannot drift. Direct OpenExisting calls found in: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void No_await_appears_inside_a_mutation_mutex_critical_section()
    {
        // Mutex is thread-affine: an await continuation may resume on another thread, so
        // ReleaseMutex can fail even inside a correct try/finally, leaving the mutex held until
        // process exit. The ref struct blocks the common shapes at compile time; this catches a
        // critical section that was made async some other way.
        var offenders = new List<string>();

        foreach (string file in CoreSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));
            string[] lines = code.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("MutationLock", StringComparison.Ordinal)
                    || !lines[i].Contains("Acquire", StringComparison.Ordinal))
                {
                    continue;
                }

                // Scan forward to the end of the enclosing scope by brace depth, starting from
                // the acquisition. An await anywhere in that region is the defect.
                int depth = 0;
                bool started = false;

                for (int j = i; j < lines.Length; j++)
                {
                    foreach (char c in lines[j])
                    {
                        if (c == '{')
                        {
                            depth++;
                            started = true;
                        }
                        else if (c == '}')
                        {
                            depth--;
                        }
                    }

                    if (Regex.IsMatch(lines[j], @"(^|[^\w])await[^\w]"))
                    {
                        offenders.Add($"{Path.GetFileName(file)} line {j + 1}");
                    }

                    if (started && depth <= 0)
                    {
                        break;
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A mutation-mutex critical section must be entirely synchronous. Found await near: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void The_mutation_lock_is_a_ref_struct_so_it_cannot_cross_an_await()
    {
        Type lockType = typeof(MutationMutex).Assembly.GetType("OutlookWidget.Core.Refresh.MutationLock")!;

        Assert.NotNull(lockType);

        // A ref struct cannot be captured in a closure, stored in a field, boxed, or held across
        // an await. That is the compile-time enforcement of thread affinity, and a future edit
        // that changes this declaration to a plain struct would silently remove it.
        Assert.True(lockType.IsValueType);
        Assert.True(
            lockType.GetCustomAttributesData().Any(a =>
                a.AttributeType.Name == "IsByRefLikeAttribute"),
            "MutationLock must remain a ref struct.");
    }

    [Fact]
    public void The_widget_host_is_only_ever_called_through_the_delivery_sink()
    {
        // Only the provider may call UpdateWidget, from the single serialized delivery worker.
        // The companion commits and signals; it never delivers. A second call site anywhere would
        // reintroduce the interleaving the worker exists to remove.
        var offenders = new List<string>();

        foreach (string file in CoreSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (code.Contains("UpdateWidget", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        // The core is surface-agnostic and must not reach the widget host at all; delivery goes
        // through IWidgetDeliverySink, implemented only in the provider.
        Assert.True(
            offenders.Count == 0,
            "OutlookWidget.Core must not reference UpdateWidget directly. Found in: "
                + string.Join(", ", offenders));
    }

    /// <summary>
    /// The name of the one file permitted to call the widget host.
    /// </summary>
    private const string DeliverySinkFileName = "WidgetDeliverySink.cs";

    [Fact]
    public void Exactly_one_provider_file_calls_UpdateWidget()
    {
        // The companion-side rule above says the core may not reach the host at all. This is the
        // other half: within the provider, which may, the call must exist in exactly one place.
        //
        // Both halves are needed. Core having no reference proves the companion cannot deliver;
        // it says nothing about a second call site being added inside the provider for
        // convenience — a refresh handler calling UpdateWidget directly, say — which would put two
        // host calls in flight and let a slow older payload land after a newer logout. A payload
        // already handed to the host cannot be retracted, so this is enforced by counting call
        // sites rather than by validating after the fact.
        var offenders = new List<string>();
        bool sinkFound = false;

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (!code.Contains("UpdateWidget", StringComparison.Ordinal))
            {
                continue;
            }

            if (Path.GetFileName(file).Equals(DeliverySinkFileName, StringComparison.Ordinal))
            {
                sinkFound = true;
                continue;
            }

            offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            $"Only {DeliverySinkFileName} may call UpdateWidget. Found in: "
                + string.Join(", ", offenders));

        // And the sink must still be the one doing it. Without this, deleting the call entirely
        // would pass a test whose whole subject is where that call lives.
        Assert.True(
            sinkFound,
            $"{DeliverySinkFileName} no longer calls UpdateWidget. Either the single delivery call "
                + "site moved, in which case this test's expectation must move with it, or widget "
                + "delivery has been removed.");
    }

    [Fact]
    public void The_delivery_sink_re_reads_disclosure_before_calling_the_host()
    {
        // Invariant 8 requires disclosure to be re-read immediately before the host call. The
        // worker reads it once per pass and hands over one DeliveryState, which satisfies that only
        // for the first instance: UpdateWidget is synchronous with no timeout, so a wedged host can
        // park the loop on instance one while a logout or hide-details commit lands, and every
        // later instance would receive the pre-tombstone payload. Those calls have not reached the
        // host yet, so withholding them is still possible.
        //
        // Checked by source order rather than behaviour because the sink lives in the provider,
        // which this project cannot reference without adopting the provider's target framework.
        // The ordering is the whole property, so asserting it textually is not much weaker than
        // asserting it dynamically would be.
        string sink = StripCommentsAndStrings(File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, DeliverySinkFileName)));

        int reRead = sink.IndexOf("_readDisclosureMode()", StringComparison.Ordinal);
        int hostCall = sink.IndexOf("UpdateWidget(", StringComparison.Ordinal);

        Assert.True(
            reRead >= 0,
            $"{DeliverySinkFileName} must re-read the effective disclosure mode through its "
                + "injected reader. If the mechanism was renamed, this expectation must move with "
                + "it rather than being deleted.");

        Assert.True(hostCall >= 0, $"{DeliverySinkFileName} no longer calls UpdateWidget.");

        Assert.True(
            reRead < hostCall,
            "The disclosure re-read must precede the UpdateWidget call. Reading it afterwards, or "
                + "only once before the loop, reintroduces the window where a suppressed payload "
                + "is delivered to an instance the host had not yet been given.");
    }

    [Fact]
    public void The_provider_locates_state_only_through_the_packaged_state_guard()
    {
        // CoordinationPaths.Resolve accepts a null family name and answers with the ordinary
        // per-user path. That is correct for unpackaged callers such as unit tests, and wrong for
        // the provider, which must refuse to run without package identity because state outside the
        // package store survives uninstall.
        //
        // The provider called Resolve directly and passed a possibly-null identity into it, which is
        // how the fallback got in. PackagedState is the guarded composition; this asserts the
        // provider cannot go around it again. Enforced by source because the alternative is
        // launching the COM server.
        var offenders = new List<string>();

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (code.Contains("CoordinationPaths.Resolve", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The provider must locate state through PackagedState.Locate, which refuses an "
                + "unpackaged process, rather than calling CoordinationPaths.Resolve with an "
                + "identity that may be null. Found direct calls in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_provider_has_no_reference_to_interactive_authentication()
    {
        // Section 3 requires the provider to be silent-only and to fail closed: broker- or
        // UI-required failures become a signed-out card with an action to open the companion, not
        // an authentication prompt. A prompt from the provider would be a window with no owner,
        // raised by a process the user did not start, over whatever they were doing.
        //
        // The core check nearby keeps the interactive API out of shared code. This keeps it out of
        // the provider itself, which is the process that must never show it.
        var offenders = new List<string>();

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (code.Contains("AcquireTokenInteractive", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The provider must have no interactive authentication path. Found in: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void No_provider_call_site_uses_the_parameterless_WaitOne()
    {
        // The provider blocks on the last-widget-deleted signal and on named events. Every wait
        // that can be contended must be bounded for the same reason it must be in the core: an
        // indefinite wait has no recovery path. The one deliberate exception is the process's
        // own shutdown wait, which is a ManualResetEventSlim rather than a mutex and is the
        // process's entire reason to remain alive.
        var prohibited = new Regex(@"\.WaitOne\s*\(\s*\)", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            foreach (Match match in prohibited.Matches(code))
            {
                offenders.Add($"{Path.GetFileName(file)} at offset {match.Index}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Every mutex acquisition must pass a timeout. Parameterless WaitOne() found in: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void The_core_has_no_reference_to_interactive_authentication()
    {
        // The provider must have no reference or code path to AcquireTokenInteractive and must
        // fail closed. Keeping the interactive API out of the shared core is what makes that
        // enforceable rather than aspirational — the companion's own interactive service is the
        // only place it may appear, and it does not exist yet.
        var offenders = new List<string>();

        foreach (string file in CoreSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (code.Contains("AcquireTokenInteractive", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Interactive authentication must not appear in shared code reachable from the provider. "
                + "Found in: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The one file permitted to acquire a token interactively.
    /// </summary>
    private const string InteractiveAuthFileName = "InteractiveAuthService.cs";

    [Fact]
    public void Exactly_one_companion_file_acquires_a_token_interactively()
    {
        // The two checks nearby establish that the interactive API appears in neither the core nor the
        // provider. On their own that is satisfied by it existing nowhere at all, which was true until
        // gate 8 needed it and is a weaker property than intended: what the invariant actually says is
        // that interactive authentication exists in exactly one place, and that the place is the
        // companion.
        //
        // This is the positive half. It is also the assertion that fails if someone "fixes" a
        // sign-in-required card by moving the interactive call somewhere the provider can reach, which
        // is the specific mistake that would put an unowned authentication window over whatever the
        // user was doing — raised by a process they did not start.
        var offenders = new List<string>();
        bool serviceFound = false;

        foreach (string file in RepositorySources.AppSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (!code.Contains("AcquireTokenInteractive", StringComparison.Ordinal))
            {
                continue;
            }

            if (Path.GetFileName(file).Equals(InteractiveAuthFileName, StringComparison.Ordinal))
            {
                serviceFound = true;
                continue;
            }

            offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            $"Only {InteractiveAuthFileName} may acquire a token interactively. Found in: "
                + string.Join(", ", offenders));

        Assert.True(
            serviceFound,
            $"{InteractiveAuthFileName} no longer acquires a token interactively. Either the single "
                + "interactive call site moved, in which case this expectation must move with it, or "
                + "the companion can no longer sign anyone in — which would make gate 8 unprovable and "
                + "gate 9 unreachable, since the provider can only acquire silently against a token "
                + "the broker already holds.");
    }

    [Fact]
    public void The_provider_reaches_the_broker_only_through_the_zero_handle_helper()
    {
        // Gate 9's subject is a zero parent window handle, and the provider must be the process that
        // supplies one. BrokerClient.NoParentWindow is a named member specifically so this is
        // searchable and so the choice cannot be made accidentally: a provider that grew a window and
        // passed its handle would be a background COM server capable of parenting a dialog, which is
        // the thing section 7 forbids.
        //
        // Checked by source because asserting it dynamically would mean launching the COM server.
        var callers = new List<string>();

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (!code.Contains("BrokerClient", StringComparison.Ordinal))
            {
                continue;
            }

            callers.Add(Path.GetFileName(file));

            Assert.True(
                code.Contains("BrokerClient.NoParentWindow", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} builds a broker client without passing "
                    + "BrokerClient.NoParentWindow. The provider owns no window and must pass no "
                    + "handle.");

            Assert.False(
                code.Contains("WithParentActivityOrWindow", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} configures a parent window directly. Parent-window "
                    + "configuration belongs to BrokerClient, which both processes share.");
        }

        Assert.True(
            callers.Count > 0,
            "No provider file builds a broker client. Either silent acquisition was removed from the "
                + "provider, which makes gate 9 unprovable, or the composition moved and this "
                + "expectation must move with it.");
    }

    [Fact]
    public void The_provider_re_probes_authentication_when_state_changes()
    {
        // The provider probes once per process and lives until its last widget is unpinned, so without
        // a re-probe on the state-changed signal the ordinary flow never converges: a widget rendering
        // "sign in required" launches the companion, the user signs in, and the provider keeps its
        // original result with a valid token sitting in the broker. The only escapes were unpinning the
        // widget or killing the process, and unpinning discards the pin that the force-shutdown upgrade
        // path exists to preserve.
        //
        // Checked by source because asserting it dynamically would mean launching the COM server and
        // driving a real broker.
        string composition = string.Empty;

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (code.Contains("new StateChangeListener", StringComparison.Ordinal))
            {
                composition = code;
                break;
            }
        }

        Assert.False(
            string.IsNullOrEmpty(composition),
            "No provider file constructs a StateChangeListener. Either cross-process notification was "
                + "removed from the provider, or the composition moved and this expectation must move "
                + "with it.");

        Assert.Contains("RequestProbe", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_wires_real_Graph_reads_through_the_refresh_transaction()
    {
        string composition = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "Program.cs"));
        string provider = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "WidgetProvider.cs"));

        Assert.Contains("new GraphMailClient", composition, StringComparison.Ordinal);
        Assert.Contains("new MailboxRefreshFetcher", composition, StringComparison.Ordinal);
        Assert.Contains("new RefreshCoordinator", composition, StringComparison.Ordinal);
        Assert.Contains("graph.ReadAsync", composition, StringComparison.Ordinal);
        Assert.Contains("includeFocusedCount: true", composition, StringComparison.Ordinal);

        Assert.Contains("RequestIfStale(RefreshTrigger.Activation)", provider, StringComparison.Ordinal);
        Assert.Contains("Request(RefreshTrigger.ManualAction)", provider, StringComparison.Ordinal);
        Assert.Contains("SetActive(true)", provider, StringComparison.Ordinal);
        Assert.Contains("SetActive(_registry.HasActiveInstances)", provider, StringComparison.Ordinal);
        Assert.Contains("RequestIfStale(RefreshTrigger.SignIn)", composition, StringComparison.Ordinal);
        Assert.Contains("Request(RefreshTrigger.ActiveTimer)", composition, StringComparison.Ordinal);
        Assert.Contains("selectedAccounts: selectedAccounts", composition, StringComparison.Ordinal);

        string refreshWorker = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "ProviderRefreshWorker.cs"));
        Assert.Contains("_selectedAccounts.Read()", refreshWorker, StringComparison.Ordinal);
        Assert.Contains("snapshot.HomeAccountId", refreshWorker, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_provider_refreshes_request_delivery_for_changed_authentication_state()
    {
        string worker = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "ProviderRefreshWorker.cs"));

        Assert.Contains("result.Delivery == DeliveryRequestOutcome.NotRequested", worker, StringComparison.Ordinal);
        Assert.Contains("_delivery.RequestDelivery()", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void A_faulted_provider_client_construction_can_be_retried()
    {
        string probe = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "SilentAuthProbe.cs"));

        Assert.Contains("ReferenceEquals(_client, clientTask)", probe, StringComparison.Ordinal);
        Assert.Contains("_client = null", probe, StringComparison.Ordinal);
        Assert.Contains("throw;", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Silent_acquisition_pairs_the_token_with_the_account_that_was_selected()
    {
        string service = File.ReadAllText(
            Path.Combine(
                RepositorySources.CoreSourceDirectory,
                "Authentication",
                "SilentAuthService.cs"));

        Assert.Contains("account.HomeAccountId?.Identifier", service, StringComparison.Ordinal);
        Assert.DoesNotContain("result.Account.HomeAccountId", service, StringComparison.Ordinal);
    }

    [Fact]
    public void The_logging_api_exposes_no_field_capable_of_carrying_mailbox_or_identity_metadata()
    {
        MethodInfo record = typeof(IOperationalLogger).GetMethod(nameof(IOperationalLogger.Record))!;
        Assert.NotNull(record);

        // The rule is enforced by API shape rather than by a growing redaction subsystem. Mailbox
        // and identity metadata are strings, so an API with no string parameter cannot accept
        // them: a call site that wants to log a subject line has nowhere to put it.
        foreach (ParameterInfo parameter in record.GetParameters())
        {
            Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

            bool permitted = type.IsEnum
                             || type == typeof(int)
                             || type == typeof(TimeSpan);

            Assert.True(
                permitted,
                $"IOperationalLogger.Record parameter '{parameter.Name}' has type "
                    + $"{parameter.ParameterType}. Only closed enums and bounded numbers are "
                    + "permitted; a string parameter would allow a subject, sender, or account "
                    + "to be logged.");
        }

        // And no other members may reintroduce one.
        foreach (MethodInfo method in typeof(IOperationalLogger).GetMethods())
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                p => (Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType) == typeof(string));
        }
    }

    [Fact]
    public void The_logger_accepts_no_exception_because_exception_text_carries_metadata()
    {
        // An exception message routinely contains a URL, an account, or a raw server response, so
        // there is deliberately nowhere to pass one.
        foreach (MethodInfo method in typeof(IOperationalLogger).GetMethods())
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                p => typeof(Exception).IsAssignableFrom(p.ParameterType));
        }
    }

    [Fact]
    public void The_lease_horizon_exceeds_the_worst_case_refresh_transaction()
    {
        // A lease expiring mid-commit would let a peer start a second refresh whose commit races
        // the first. The generation compare would still prevent corruption, but the wasted
        // request and the confusing indicator state are avoidable by choosing the horizon
        // correctly.
        Assert.True(
            CoordinationBounds.LeaseHorizon > CoordinationBounds.WorstCaseRefreshTransaction,
            $"Lease horizon {CoordinationBounds.LeaseHorizon} must exceed the worst case "
                + $"{CoordinationBounds.WorstCaseRefreshTransaction}.");

        // And the relationship is asserted at startup, not merely here, so a bad edit fails
        // locally rather than as a rare production race.
        CoordinationBounds.Validate();
    }

    [Fact]
    public void The_graph_timeout_is_nested_inside_the_async_deadline()
    {
        Assert.True(CoordinationBounds.GraphRequestTimeout < CoordinationBounds.AsyncDeadline);
    }

    [Fact]
    public void Every_documented_bound_matches_the_plan()
    {
        // These specific numbers are load-bearing and are quoted in the plan's bounds table, the
        // nonfunctional targets, and the troubleshooting guide. Changing one without changing the
        // others is how a document and its implementation drift apart.
        Assert.Equal(TimeSpan.FromSeconds(2), CoordinationBounds.MutexWait);
        Assert.Equal(TimeSpan.FromSeconds(20), CoordinationBounds.AsyncDeadline);
        Assert.Equal(TimeSpan.FromSeconds(10), CoordinationBounds.GraphRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), CoordinationBounds.LeaseHorizon);
        Assert.Equal(TimeSpan.FromSeconds(15), CoordinationBounds.ManualRefreshDebounce);
        Assert.Equal(TimeSpan.FromSeconds(60), CoordinationBounds.ActivationStaleness);
        Assert.Equal(TimeSpan.FromMinutes(5), CoordinationBounds.ActiveTimerInterval);
        Assert.Equal(TimeSpan.FromHours(24), CoordinationBounds.StaleDetailSuppression);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)],
            CoordinationBounds.ReplaceRetryBackoff);

        // 2 + 20 + 2 + 1 + 2 = 27 seconds, the figure the plan's table reports.
        Assert.Equal(TimeSpan.FromSeconds(27), CoordinationBounds.WorstCaseRefreshTransaction);
    }

    [Fact]
    public void Disclosure_modes_are_ordered_so_the_strongest_present_wins_by_comparison()
    {
        // The numeric ordering is load-bearing: the effective mode is the maximum of the modes
        // present, which is what makes per-operation files safe from a lost update.
        Assert.True(DisclosureMode.SignedOut > DisclosureMode.CountsOnly);
        Assert.True(DisclosureMode.CountsOnly > DisclosureMode.Full);
        Assert.Equal(0, (int)DisclosureMode.Full);
    }
}
