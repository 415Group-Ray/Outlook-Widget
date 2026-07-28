using System.Reflection;
using System.Text.RegularExpressions;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

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
    private static string CoreSourceDirectory
    {
        get
        {
            string? configured = typeof(CoordinationStaticAnalysisTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "CoreSourceDirectory")
                ?.Value;

            Assert.False(
                string.IsNullOrWhiteSpace(configured),
                "The CoreSourceDirectory assembly metadata is missing; these checks cannot run.");

            string resolved = Path.GetFullPath(configured!);
            Assert.True(Directory.Exists(resolved), $"Core source directory not found: {resolved}");
            return resolved;
        }
    }

    private static IEnumerable<string> CoreSourceFiles() =>
        Directory.EnumerateFiles(CoreSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase)
                   && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Strips line comments, block comments, and string literals, so a rule never fires on prose
    /// that merely discusses the prohibited construction. These files document their own
    /// invariants at length, so this matters: without it, every check here would fail on its own
    /// explanation.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            // Block comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }

            // Line comment, which also covers /// documentation
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
                continue;
            }

            // Verbatim string
            if (i + 1 < source.Length && source[i] == '@' && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            // Ordinary string
            if (source[i] == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            output.Append(source[i]);
            i++;
        }

        return output.ToString();
    }

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
