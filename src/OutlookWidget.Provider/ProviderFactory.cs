using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace OutlookWidget.Provider;

/// <summary>
/// The COM class object the Widgets host activates to obtain the provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a factory rather than a parameterless provider type.</b> The documented sample
/// constrains its factory to <c>where T : IWidgetProvider, new()</c> and calls <c>new T()</c>,
/// which forces every dependency to be resolved from static state. This provider needs a shared
/// coordination stack — one cache, one tombstone store, one serialized delivery worker — created
/// once at process start and reachable from the single delivery call site. So the factory takes a
/// delegate instead, and composition stays in <c>Program</c> where the disposal order is visible.
/// </para>
/// <para>
/// <b>The accepted interface IDs are a deliberate superset of the sample's.</b> The sample
/// accepts only <c>IUnknown</c> and the CLR-generated GUID of its provider class, the latter of
/// which is not the CLSID and is not something a caller would ask for. Accepting
/// <c>IUnknown</c>, <c>IInspectable</c>, and <c>IWidgetProvider</c> covers what a WinRT
/// out-of-process activation actually requests. It is a superset, so it cannot refuse an
/// activation the documented sample would have accepted, and
/// <see cref="MarshalInspectable{T}.FromManaged"/> returns a pointer that supports
/// <c>QueryInterface</c> for the rest.
/// </para>
/// </remarks>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ProviderFactory : IClassFactory
{
    private const int S_OK = 0;
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    private static readonly Guid IUnknownIid = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IInspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    private readonly Func<IWidgetProvider> _create;

    public ProviderFactory(Func<IWidgetProvider> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
    }

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        // Aggregation is not supported. Returning the HRESULT rather than throwing keeps the
        // failure inside COM's error model: this method is [PreserveSig], so an exception
        // escaping it would cross the native boundary as an unhandled managed exception and kill
        // the provider process instead of failing one activation.
        if (pUnkOuter != IntPtr.Zero)
        {
            return CLASS_E_NOAGGREGATION;
        }

        if (riid != IUnknownIid && riid != IInspectableIid && riid != typeof(IWidgetProvider).GUID)
        {
            return E_NOINTERFACE;
        }

        try
        {
            ppvObject = MarshalInspectable<IWidgetProvider>.FromManaged(_create());
            return S_OK;
        }
        catch (Exception)
        {
            // The provider could not be constructed. The host sees a failed activation and can
            // retry; the process stays alive to serve any instances it already has.
            return E_UNEXPECTED;
        }
    }

    /// <summary>
    /// Deliberately a no-op returning success.
    /// </summary>
    /// <remarks>
    /// Server lifetime here is governed by having at least one enabled widget, not by COM lock
    /// counts: the provider exits after the last instance is deleted and lives as long as any
    /// remain. Incrementing a lock count would add a second, independent lifetime rule that could
    /// disagree with that one, and the disagreement would surface as a provider that either exits
    /// while widgets are pinned or never exits at all.
    /// </remarks>
    public int LockServer(bool fLock) => S_OK;
}

/// <summary>
/// <c>IClassFactory</c>, declared here because .NET does not project it.
/// </summary>
/// <remarks>
/// <c>InterfaceIsIUnknown</c> and <c>PreserveSig</c> are both load-bearing. Without the former the
/// runtime would assume IDispatch and lay the vtable out wrongly; without the latter it would
/// translate a failure HRESULT into a managed exception on the way out, which is not what a COM
/// caller expects to receive.
/// </remarks>
[ComImport]
[ComVisible(false)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
