using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Р Р°Р·Р±РѕСЂ WebP С‡РµСЂРµР· LovitWebP.dll.
    ///
    /// CDN РјРµСЃСЃРµРЅРґР¶РµСЂР° MAX РѕС‚РґР°С‘С‚ РёР·РѕР±СЂР°Р¶РµРЅРёСЏ С‚РѕР»СЊРєРѕ РІ WebP: Рё Р°РІР°С‚Р°СЂРєРё,
    /// Рё С„РѕС‚РѕРіСЂР°С„РёРё, Рё СЃС‚РёРєРµСЂС‹. Windows Phone 8.1 С‚Р°РєРѕРіРѕ С„РѕСЂРјР°С‚Р° РЅРµ Р·РЅР°РµС‚
    /// РІРѕРІСЃРµ вЂ” РЅРё BitmapDecoder, РЅРё BitmapImage РµРіРѕ РЅРµ РѕС‚РєСЂС‹РІР°СЋС‚, Рё Р±РµР·
    /// СЃРІРѕРµРіРѕ СЂР°Р·Р±РѕСЂР° РІ РїСЂРёР»РѕР¶РµРЅРёРё РЅРµ Р±С‹Р»Рѕ Р±С‹ РІРёРґРЅРѕ РЅРё РѕРґРЅРѕРіРѕ Р»РёС†Р°.
    /// </summary>
    public static class WebPImage
    {
        private const string Library = "LovitWebP.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_webp_is_webp(byte[] data, int size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_webp_decode(byte[] data, int size,
                                                       out int width, out int height);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_webp_decode_scaled(byte[] data, int size, int maxSide,
                                                              out int width, out int height);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_webp_free(IntPtr pixels);

        private static bool? _available;

        /// <summary>Р•СЃС‚СЊ Р»Рё СЂР°Р·Р±РѕСЂ РІ СЌС‚РѕР№ СЃР±РѕСЂРєРµ: Р±РёР±Р»РёРѕС‚РµРєР° СЃРѕР±СЂР°РЅР° РЅРµ РґР»СЏ РІСЃРµС….</summary>
        public static bool IsAvailable
        {
            get
            {
                if (_available.HasValue) return _available.Value;

                try
                {
                    _available = lovit_webp_is_webp(new byte[12], 12) == 0;
                }
                catch (Exception)
                {
                    _available = false;
                }

                return _available.Value;
            }
        }

        public static bool IsWebP(byte[] data)
        {
            if (data == null || data.Length < 12) return false;
            if (!IsAvailable) return false;

            return lovit_webp_is_webp(data, data.Length) != 0;
        }

        /// <summary>
        /// РџРёРєСЃРµР»Рё BGRA СЃ РґРѕРјРЅРѕР¶РµРЅРЅРѕР№ РїСЂРѕР·СЂР°С‡РЅРѕСЃС‚СЊСЋ вЂ” РІ С‚РѕРј РІРёРґРµ, РІ РєР°РєРѕРј РёС…
        /// Р¶РґС‘С‚ WriteableBitmap. РќРѕР»СЊ РІРјРµСЃС‚Рѕ РєР°СЂС‚РёРЅРєРё РѕР·РЅР°С‡Р°РµС‚, С‡С‚Рѕ СЂР°Р·РѕР±СЂР°С‚СЊ
        /// РЅРµ РІС‹С€Р»Рѕ.
        /// </summary>
        public static Pixels Decode(byte[] data)
        {
            return Decode(data, 0);
        }

        /// <summary>
        /// РўРѕ Р¶Рµ, РЅРѕ РЅРµ РєСЂСѓРїРЅРµРµ `maxSide` РїРѕ Р±РѕР»СЊС€РµР№ СЃС‚РѕСЂРѕРЅРµ. РќРѕР»СЊ РѕР·РЅР°С‡Р°РµС‚
        /// В«РєР°Рє РµСЃС‚СЊВ».
        ///
        /// РЈРјРµРЅСЊС€РµРЅРёРµ Р·РґРµСЃСЊ РЅРµ РїСЂРѕ РєР°С‡РµСЃС‚РІРѕ, Р° РїСЂРѕ С‚Рѕ, С‡С‚Рѕ РєР°СЂС‚РёРЅРєСѓ РЅРµРіРґРµ
        /// РїРѕРєР°Р·Р°С‚СЊ С†РµР»РёРєРѕРј. РђРІР°С‚Р°СЂРєР° РІ СЃРїРёСЃРєРµ Р·Р°РЅРёРјР°РµС‚ СЃРµРјСЊРґРµСЃСЏС‚ РґРІРµ С‚РѕС‡РєРё,
        /// Р° РїСЂРёС…РѕРґРёС‚ СЃ CDN РІ РїРѕР»С‚РѕСЂС‹ С‚С‹СЃСЏС‡Рё: СЂР°Р·РѕР±СЂР°РЅРЅР°СЏ, РѕРЅР° РІРµСЃРёС‚ РІРѕСЃРµРјСЊ
        /// РјРµРіР°Р±Р°Р№С‚ РІРјРµСЃС‚Рѕ СЃС‚Р° РєРёР»РѕР±Р°Р№С‚. РЎРѕСЂРѕРє С‡Р°С‚РѕРІ вЂ” Рё РїСЂРёР»РѕР¶РµРЅРёРµ СѓР¶Рµ Р·Р°
        /// РїСЂРµРґРµР»РѕРј, РѕС‚РІРµРґС‘РЅРЅС‹Рј РµРјСѓ С‚РµР»РµС„РѕРЅРѕРј. РЈРјРµРЅСЊС€Р°РµРј РґРѕ С‚РѕРіРѕ СЂР°Р·РјРµСЂР°, РІ
        /// РєРѕС‚РѕСЂРѕРј РµС‘ Рё РїРѕРєР°Р¶СѓС‚: РјРµРЅСЊС€Рµ РїРѕРєР°Р·Р°РЅРЅРѕРіРѕ РЅРµ Р±РµСЂС‘Рј РЅРёРєРѕРіРґР°, С‚Р°Рє
        /// С‡С‚Рѕ РЅР° РіР»Р°Р· СЂР°Р·РЅРёС†С‹ РЅРµС‚.
        /// </summary>
        public static Pixels Decode(byte[] data, int maxSide)
        {
            if (!IsWebP(data)) return null;

            int width;
            int height;

            // РЈРјРµРЅСЊС€РµРЅРёРµРј Р·Р°РЅСЏС‚Р° СЃР°РјР° Р±РёР±Р»РёРѕС‚РµРєР°: РѕРЅР° РґРµР»Р°РµС‚ СЌС‚Рѕ РїРѕ С…РѕРґСѓ
            // СЂР°Р·Р±РѕСЂР°, Рё РїРѕР»РЅРѕР№ РєР°СЂС‚РёРЅРєРё РЅРµ РїРѕСЏРІР»СЏРµС‚СЃСЏ РЅРё РјРіРЅРѕРІРµРЅРёСЏ. РќР°С€Рµ
            // РїСЂРµР¶РЅРµРµ СѓРјРµРЅСЊС€РµРЅРёРµ СЂР°Р±РѕС‚Р°Р»Рѕ СѓР¶Рµ РїРѕ РіРѕС‚РѕРІРѕР№ вЂ” С‚Рѕ РµСЃС‚СЊ РїРѕСЃР»Рµ
            // С‚РѕРіРѕ, РєР°Рє РІРѕСЃРµРјСЊ РјРµРіР°Р±Р°Р№С‚ СѓР¶Рµ СЂР°Р·РІРµСЂРЅСѓР»РёСЃСЊ РІ РїР°РјСЏС‚Рё, Рё РѕС‚
            // СЃР°РјРѕР№ РґРѕСЂРѕРіРѕР№ С‡Р°СЃС‚Рё РЅРµ СЃРїР°СЃР°Р»Рѕ.
            IntPtr buffer = lovit_webp_decode_scaled(data, data.Length, maxSide,
                                                     out width, out height);

            if (buffer == IntPtr.Zero || width <= 0 || height <= 0) return null;

            try
            {
                return Whole(buffer, width, height);
            }
            finally
            {
                // РћСЃРІРѕР±РѕР¶РґР°С‚СЊ СЃРІРѕРёРј СЃРїРѕСЃРѕР±РѕРј РЅРµР»СЊР·СЏ: libwebp РІС‹РґРµР»СЏРµС‚ Р±СѓС„РµСЂ
                // СЃРѕР±СЃС‚РІРµРЅРЅС‹Рј СЂР°СЃРїСЂРµРґРµР»РёС‚РµР»РµРј.
                lovit_webp_free(buffer);
            }
        }

        private static Pixels Whole(IntPtr buffer, int width, int height)
        {
            var bytes = new byte[width * height * 4];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);

            return new Pixels { Width = width, Height = height, Bgra = bytes };
        }

        public class Pixels
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public byte[] Bgra { get; set; }
        }

        /// <summary>
        /// РљР°СЂС‚РёРЅРєР° РґР»СЏ РїРѕРєР°Р·Р°. Р—РѕРІС‘С‚СЃСЏ СЃ РїРѕС‚РѕРєР° СЂР°Р·РјРµС‚РєРё: WriteableBitmap вЂ”
        /// РѕР±СЉРµРєС‚ Р·Р°РІРёСЃРёРјРѕСЃС‚РµР№, Рё СЃРѕР·РґР°С‚СЊ РµРіРѕ РІ СЂР°Р±РѕС‡РµРј РїРѕС‚РѕРєРµ РЅРµР»СЊР·СЏ.
        /// </summary>
        public static async Task<WriteableBitmap> ToBitmapAsync(Pixels pixels)
        {
            if (pixels == null) return null;

            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);

            using (Stream stream = bitmap.PixelBuffer.AsStream())
            {
                await stream.WriteAsync(pixels.Bgra, 0, pixels.Bgra.Length);
            }

            bitmap.Invalidate();
            return bitmap;
        }
    }
}
