using System.Globalization;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using OutlookWidget.Core.Caching;

namespace OutlookWidget.Core.Authentication;

/// <summary>
/// Builds the one MSAL client both processes use, differing only in the parent window they supply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared construction, and the difference reduced to a single delegate.</b> Current Microsoft
/// documentation is explicit that to use the broker at all "it is now required to provide the window
/// handle to which the WAM modal dialog be parented", and that requirement sits on the
/// <em>builder</em> rather than on the interactive call. So the companion and the provider need the
/// same client configured the same way, and the only legitimate difference between them is what
/// <paramref name="parentWindowProvider"/> returns: a real top-level window in the companion, and
/// <see cref="IntPtr.Zero"/> in the provider, which has no window and must never grow one.
/// </para>
/// <para>
/// Keeping that as the sole difference is deliberate. Two separately-configured clients would drift,
/// and a drift in broker configuration is invisible until a token acquisition fails on one surface
/// and not the other. This file contains no interactive acquisition and must never acquire one — the
/// companion owns that call, and both a core-wide and a provider-wide source check enforce it.
/// </para>
/// <para>
/// <b>Whether a zero handle actually works is an unresolved gate, not an assumption.</b> Phase 0's
/// gate 9 exists to measure exactly this. Microsoft documents that WAM requires "an active,
/// interactive Windows user session" and the ability to display UI; the provider is COM-activated by
/// the Widgets host inside the signed-in user's session, so it satisfies the session requirement, but
/// no documentation states what a zero parent handle does on a silent-only call. If it fails, the
/// surface decision in section 18 reopens.
/// </para>
/// </remarks>
public static class BrokerClient
{
    /// <summary>
    /// The name WAM shows in its own dialog. User-visible, so it is the product name rather than an
    /// assembly name.
    /// </summary>
    public const string BrokerTitle = "Outlook Inbox Widget";

    /// <summary>
    /// The handle the provider passes: no window, and no possibility of one.
    /// </summary>
    /// <remarks>
    /// A named member rather than <c>() =&gt; IntPtr.Zero</c> written inline at the call site, so the
    /// provider's composition reads as a deliberate choice that can be searched for, and so this
    /// comment sits next to it. The provider process runs no message loop and owns no window; there
    /// is nothing else it could truthfully supply.
    /// </remarks>
    public static IntPtr NoParentWindow() => IntPtr.Zero;

    /// <summary>
    /// Creates the public client application and attaches the shared, DPAPI-protected token cache.
    /// </summary>
    /// <param name="options">The Entra registration this build authenticates against.</param>
    /// <param name="paths">
    /// Where the shared token cache lives. Inside the package store, so uninstall removes the account
    /// metadata along with the mail cache.
    /// </param>
    /// <param name="parentWindowProvider">
    /// Supplies the parent window handle. Pass <see cref="NoParentWindow"/> from the provider.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The cache is why this method is asynchronous.</b> Nothing about building an MSAL client
    /// needs to be, but <c>MsalCacheHelper.CreateAsync</c> is the documented entry point and it
    /// performs the first cross-process lock acquisition. Attaching the cache is not optional: MSAL
    /// stores account metadata in it even though the broker holds the refresh token, so a provider
    /// running without it would enumerate no accounts and report interaction-required immediately
    /// after the companion had signed in successfully.
    /// </para>
    /// <para>
    /// <c>MsalCacheHelper.CreateAsync</c> takes no cancellation token, so none is accepted here
    /// rather than accepting one and silently ignoring it.
    /// </para>
    /// </remarks>
    public static async Task<IPublicClientApplication> CreateAsync(
        AuthenticationOptions options,
        CoordinationPaths paths,
        Func<IntPtr> parentWindowProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(parentWindowProvider);

        var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = BrokerTitle,
        };

        IPublicClientApplication client = PublicClientApplicationBuilder
            .Create(options.ClientId.ToString("D", CultureInfo.InvariantCulture))

            // Built from the tenant ID rather than read from configuration, so no file on the machine
            // can redirect authentication to another authority. See AuthenticationOptions.
            .WithAuthority(options.Authority)

            .WithParentActivityOrWindow(parentWindowProvider)
            .WithBroker(brokerOptions)

            // No ADAL cache interop is wanted or possible here, and leaving it enabled runs legacy
            // cache code on every acquisition for no benefit.
            .WithLegacyCacheCompatibility(false)
            .Build();

        // The directory must exist before the helper takes its lock; the coordination root is
        // normally created by the composition root already, and doing it again is free.
        Directory.CreateDirectory(paths.TokenCacheDirectory);

        var storage = new StorageCreationPropertiesBuilder(
                paths.TokenCacheFileName,
                paths.TokenCacheDirectory)
            .Build();

        // Windows DPAPI at rest and a cross-process lock, both from the helper. The Linux keyring
        // and macOS keychain configuration the documentation shows is deliberately absent: this
        // product is Windows-only by section 1, and configuring stores for platforms it cannot run
        // on would imply portability it does not have.
        MsalCacheHelper cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        cacheHelper.RegisterCache(client.UserTokenCache);

        return client;
    }
}
