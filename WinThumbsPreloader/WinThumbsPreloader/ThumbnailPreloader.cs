using System;
using System.Runtime.InteropServices;

namespace WinThumbsPreloader
{
    public enum ThumbnailPreloadResult
    {
        //The thumbnail was produced and is now in the thumbnail cache.
        Preloaded,
        //The shell has no thumbnail provider for this item, so there is nothing to cache.
        NoThumbnail,
        //Something went wrong, for example the item is gone or inaccessible.
        Failed
    }

    //Preload one thumbnail
    public class ThumbnailPreloader
    {
        public const uint DefaultThumbnailSize = 128;

        //HRESULTs that mean "there is no thumbnail for this item" instead of "something broke".
        //Observed with the local thumbnail cache:
        //  0x8004B200 WTS_E_CACHE_BITS_NEW_AVAIL  no thumbnail provider is registered for this file type
        //  0x80030002 WTS_E_NOTFOUND              the item is not in the thumbnail cache
        //  0x8007065E ERROR_NOT_SUPPORTED         nothing on this machine can decode the format
        public const int CacheBitsNewAvail = unchecked((int)0x8004B200);
        public const int CacheNotFound = unchecked((int)0x80030002);
        public const int FormatNotSupported = unchecked((int)0x8007065E);

        private static bool IsNoThumbnail(int hresult)
        {
            return hresult == CacheBitsNewAvail || hresult == CacheNotFound || hresult == FormatNotSupported;
        }

        private Guid iIdIShellItem;
        private IThumbnailCache TBCache;
        private uint thumbnailSize;

        public ThumbnailPreloader() : this(DefaultThumbnailSize)
        {
        }

        public ThumbnailPreloader(uint thumbnailSize)
        {
            this.thumbnailSize = thumbnailSize;
            iIdIShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            Guid CLSIDLocalThumbnailCache = new Guid("50ef4544-ac9f-4a8e-b21b-8a26180db13f");
            var TBCacheType = Type.GetTypeFromCLSID(CLSIDLocalThumbnailCache);
            TBCache = (IThumbnailCache)Activator.CreateInstance(TBCacheType);
        }

        public void PreloadThumbnail(string filePath)
        {
            Exception error;
            Preload(filePath, WTS_FLAGS.WTS_EXTRACTINPROC, out error);
        }

        public bool PreloadThumbnail(string filePath, WTS_FLAGS flags)
        {
            Exception error;
            return Preload(filePath, flags, out error) == ThumbnailPreloadResult.Preloaded;
        }

        //Asks the thumbnail cache to produce and cache a thumbnail for the item.
        //NoThumbnail means the shell has no thumbnail provider for that type of file,
        //which is a normal answer and not an error.
        public ThumbnailPreloadResult Preload(string filePath, WTS_FLAGS flags, out Exception error)
        {
            IShellItem shellItem = null;
            ISharedBitmap bmp = null;
            WTS_CACHEFLAGS cFlags;
            WTS_THUMBNAILID bmpId;
            error = null;
            try
            {
                SHCreateItemFromParsingName(filePath, IntPtr.Zero, iIdIShellItem, out shellItem);
                if (shellItem == null)
                {
                    error = new InvalidOperationException("SHCreateItemFromParsingName returned no shell item.");
                    return ThumbnailPreloadResult.Failed;
                }
                TBCache.GetThumbnail(shellItem, thumbnailSize, flags, out bmp, out cFlags, out bmpId);
                return ThumbnailPreloadResult.Preloaded;
            }
            catch (COMException e)
            {
                if (IsNoThumbnail(e.ErrorCode)) return ThumbnailPreloadResult.NoThumbnail;
                error = e;
                return ThumbnailPreloadResult.Failed;
            }
            catch (Exception e)
            {
                error = e;
                return ThumbnailPreloadResult.Failed;
            }
            finally
            {
                if (bmp != null) Marshal.ReleaseComObject(bmp);
                if (shellItem != null) Marshal.ReleaseComObject(shellItem);
            }
        }

        //Import native functions
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(
            [In][MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In][MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out][MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItem ppv);

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("F676C15D-596A-4ce2-8234-33996F445DB1")]
        interface IThumbnailCache
        {
            uint GetThumbnail(
                [In] IShellItem pShellItem,
                [In] uint cxyRequestedThumbSize,
                [In] WTS_FLAGS flags /*default:  WTS_FLAGS.WTS_EXTRACT*/,
                [Out][MarshalAs(UnmanagedType.Interface)] out ISharedBitmap ppvThumb,
                [Out] out WTS_CACHEFLAGS pOutFlags,
                [Out] out WTS_THUMBNAILID pThumbnailID
            );

            void GetThumbnailByID(
                [In, MarshalAs(UnmanagedType.Struct)] WTS_THUMBNAILID thumbnailID,
                [In] uint cxyRequestedThumbSize,
                [Out][MarshalAs(UnmanagedType.Interface)] out ISharedBitmap ppvThumb,
                [Out] out WTS_CACHEFLAGS pOutFlags
            );
        }

        [Flags]
        public enum WTS_FLAGS : uint
        {
            WTS_EXTRACT = 0x00000000,
            WTS_INCACHEONLY = 0x00000001,
            WTS_FASTEXTRACT = 0x00000002,
            WTS_SLOWRECLAIM = 0x00000004,
            WTS_FORCEEXTRACTION = 0x00000008,
            WTS_EXTRACTDONOTCACHE = 0x00000020,
            WTS_SCALETOREQUESTEDSIZE = 0x00000040,
            WTS_SKIPFASTEXTRACT = 0x00000080,
            WTS_EXTRACTINPROC = 0x00000100
        }

        [Flags]
        enum WTS_CACHEFLAGS : uint
        {
            WTS_DEFAULT = 0x00000000,
            WTS_LOWQUALITY = 0x00000001,
            WTS_CACHED = 0x00000002
        }

        [StructLayout(LayoutKind.Sequential, Size = 16), Serializable]
        struct WTS_THUMBNAILID
        {
            [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 16)]
            byte[] rgbKey;
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        public interface IShellItem
        {
            void BindToHandler(IntPtr pbc,
                [MarshalAs(UnmanagedType.LPStruct)]Guid bhid,
                [MarshalAs(UnmanagedType.LPStruct)]Guid riid,
                out IntPtr ppv);

            void GetParent(out IShellItem ppsi);

            void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);

            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

            void Compare(IShellItem psi, uint hint, out int piOrder);
        };

        public enum SIGDN : uint
        {
            NORMALDISPLAY = 0,
            PARENTRELATIVEPARSING = 0x80018001,
            PARENTRELATIVEFORADDRESSBAR = 0x8001c001,
            DESKTOPABSOLUTEPARSING = 0x80028000,
            PARENTRELATIVEEDITING = 0x80031001,
            DESKTOPABSOLUTEEDITING = 0x8004c000,
            FILESYSPATH = 0x80058000,
            URL = 0x80068000
        }

        [ComImportAttribute()]
        [GuidAttribute("091162a4-bc96-411f-aae8-c5122cd03363")]
        [InterfaceTypeAttribute(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISharedBitmap
        {
            uint Detach(
                [Out] out IntPtr phbm
            );

            uint GetFormat(
                [Out]  out WTS_ALPHATYPE pat
            );

            uint GetSharedBitmap(
                [Out] out IntPtr phbm
            );

            uint GetSize(
                [Out, MarshalAs(UnmanagedType.Struct)] out SIZE pSize
            );

            uint InitializeBitmap(
                [In]  IntPtr hbm,
                [In]  WTS_ALPHATYPE wtsAT
            );
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;

            public SIZE(int cx, int cy)
            {
                this.cx = cx;
                this.cy = cy;
            }
        }

        public enum WTS_ALPHATYPE : uint
        {
            WTSAT_UNKNOWN = 0,
            WTSAT_RGB = 1,
            WTSAT_ARGB = 2
        }
    }
}