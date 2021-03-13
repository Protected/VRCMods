using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Harmony;
using LagFreeScreenshots;
using MelonLoader;
using UIExpansionKit.API;
using UnhollowerRuntimeLib.XrefScans;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.UserCamera;
using VRC.Core;
using VRC;
using Object = UnityEngine.Object;
using CameraTakePhotoEnumerator = VRC.UserCamera.CameraUtil.ObjectNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObBoAcIn2StInTeCaUnique;
using System.Collections.Generic;
// using CameraUtil = ObjectPublicCaSiVeUnique;

[assembly:MelonInfo(typeof(LagFreeScreenshotsMod), "Lag Free Screenshots", "1.1.1", "knah", "https://github.com/knah/VRCMods")]
[assembly:MelonGame("VRChat", "VRChat")]
[assembly:MelonOptionalDependencies("UIExpansionKit")]

namespace LagFreeScreenshots
{
    public class LagFreeScreenshotsMod : MelonMod
    {
        private static readonly AwaitProvider ourToMainThread = new AwaitProvider();
        private static readonly AwaitProvider ourToEndOfFrame = new AwaitProvider();

        private const string SettingsCategory = "LagFreeScreenshots";
        private const string SettingEnableMod = "Enabled";
        private const string SettingScreenshotFormat = "ScreenshotFormat";
        private const string SettingJpegPercent = "JpegPercent";
        private const string SettingMetadata = "Metadata";

        private static MelonPreferences_Entry<bool> ourEnabled;
        private static MelonPreferences_Entry<string> ourFormat;
        private static MelonPreferences_Entry<int> ourJpegPercent;
        private static MelonPreferences_Entry<bool> ourMetadata;

        private static Thread ourMainThread;

        public override void OnApplicationStart()
        {
            var category = MelonPreferences.CreateCategory(SettingsCategory, "Lag Free Screenshots");
            ourEnabled = (MelonPreferences_Entry<bool>) category.CreateEntry(SettingEnableMod, true, "Enabled");
            ourFormat = (MelonPreferences_Entry<string>) category.CreateEntry( SettingScreenshotFormat, "png", "Screenshot format");
            ourJpegPercent = (MelonPreferences_Entry<int>) category.CreateEntry(SettingJpegPercent, 95, "JPEG quality (0-100)");
            ourMetadata = (MelonPreferences_Entry<bool>)category.CreateEntry(SettingMetadata, false, "Save metadata in picture");

            Harmony.Patch(
                typeof(CameraTakePhotoEnumerator).GetMethod("MoveNext"),
                new HarmonyMethod(AccessTools.Method(typeof(LagFreeScreenshotsMod), nameof(MoveNextPatchAsyncReadback))));
            
            if (MelonHandler.Mods.Any(it => it.Info.Name == "UI Expansion Kit" && !it.Info.Version.StartsWith("0.1."))) 
                AddEnumSettings();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddEnumSettings()
        {
            ExpansionKitApi.RegisterSettingAsStringEnum(SettingsCategory, SettingScreenshotFormat, new []{("png", "PNG"), ("jpeg", "JPEG")});
        }

        private static string GetPlayerList(Camera camera)
        {
            var playerManager = PlayerManager.field_Private_Static_PlayerManager_0;
            if (playerManager == null) return "";

            var result = new List<string>();

            var localPlayer = VRCPlayer.field_Internal_Static_VRCPlayer_0;
            var localPosition = localPlayer.gameObject.transform.position;

            foreach (var p in playerManager.field_Private_List_1_Player_0)
            {
                var playerPosition = p.gameObject.transform.position;
                Vector3 viewPos = camera.WorldToViewportPoint(playerPosition);
                var playerDescriptor = p.prop_APIUser_0.id + "," + viewPos.x.ToString("0.00") + "," + viewPos.y.ToString("0.00") + "," + viewPos.z.ToString("0.00") + "," + p.prop_APIUser_0.displayName;
                
                if (viewPos.z < 2 && Vector3.Distance(localPosition, playerPosition) < 2) {
                    //User standing right next to photographer, might be visible (approx.)
                    result.Add(playerDescriptor);
                }
                else if (viewPos.x > -0.03 && viewPos.x < 1.03 && viewPos.y > -0.03 && viewPos.y < 1.03 && viewPos.z > 2 && viewPos.z < 30)
                {
                    //User in viewport, might be obstructed but still...
                    result.Add(playerDescriptor);
                }
            }

            return String.Join(";", result);
        }

        private static string GetPhotographerMeta()
        {
            return APIUser.CurrentUser.id + "," + APIUser.CurrentUser.displayName;
        }

        private static string GetWorldMeta()
        {
            return RoomManager.field_Internal_Static_ApiWorld_0.id + "," + RoomManager.field_Internal_Static_ApiWorldInstance_0.idOnly + "," + RoomManager.field_Internal_Static_ApiWorld_0.name;
        }

        public override void OnUpdate()
        {
            ourToMainThread.Flush();
        }

        public override void OnGUI()
        {
            ourToEndOfFrame.Flush();
        }

        public static bool MoveNextPatchAsyncReadback(ref bool __result, CameraTakePhotoEnumerator __instance)
        {
            var resX = __instance.field_Public_Int32_0;
            var resY = __instance.field_Public_Int32_1;
            
            // ignore low resultion, unchanged pngs - it's fast enough and also used by VRC+ picture features
            if (!ourEnabled.Value || resX <= 1920 && resY <= 1080 && ourFormat.Value == "png" && !ourMetadata.Value)
                return true;
            
            ourMainThread = Thread.CurrentThread;

            __result = false;
            TakeScreenshot(__instance.field_Public_Camera_0, resX,
                resY, true).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    MelonLogger.Warning($"Free-floating task failed with exception: {t.Exception}");
            });
            return false;
        }
        
        public static async Task TakeScreenshot(Camera camera, int w, int h, bool hasAlpha)
        {
            await ourToEndOfFrame.Yield();

            MelonLogger.Msg("Photographing with LFS");

            // var renderTexture = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 8);
            var renderTexture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            renderTexture.antiAliasing = 8;

            var oldCameraTarget = camera.targetTexture;
            var oldCameraFov = camera.fieldOfView;

            camera.targetTexture = renderTexture;
            
            camera.Render();

            camera.targetTexture = oldCameraTarget;
            camera.fieldOfView = oldCameraFov;

            (IntPtr, int) data = default;
            var readbackSupported = SystemInfo.supportsAsyncGPUReadback;
            if (readbackSupported)
            {
                MelonDebug.Msg("Supports readback");
                
                var stopwatch = Stopwatch.StartNew();
                var request = AsyncGPUReadback.Request(renderTexture, 0, hasAlpha ? TextureFormat.ARGB32 : TextureFormat.RGB24,new Action<AsyncGPUReadbackRequest>(r =>
                {
                    if (r.hasError)
                        MelonLogger.Warning("Readback request finished with error (w)");
                    
                    data = ToBytes(r.GetDataRaw(0), r.GetLayerDataSize());
                    MelonDebug.Msg($"Bytes readback took total {stopwatch.ElapsedMilliseconds}");
                }));
                
                while (!request.done && !request.hasError && data.Item1 == IntPtr.Zero)
                    await ourToMainThread.Yield();

                if (request.hasError)
                    MelonLogger.Warning("Readback request finished with error");
                
                if (data.Item1 == IntPtr.Zero)
                {
                    MelonDebug.Msg("Data was null after request was done, waiting more");
                    await ourToMainThread.Yield();
                }
            }
            else
            {
                MelonLogger.Msg("Does not support readback, using fallback texture read method");
                
                RenderTexture.active = renderTexture;
                var newTexture = new Texture2D(w, h, hasAlpha ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);
                newTexture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                newTexture.Apply();
                RenderTexture.active = null;

                var bytes = newTexture.GetRawTextureData();
                data = (Marshal.AllocHGlobal(bytes.Length), bytes.Length);
                Il2CppSystem.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Item1, bytes.Length);
                
                Object.Destroy(newTexture);
            }
            
            Object.Destroy(renderTexture);

            var targetFile = GetPath(w, h);
            var targetDir = Path.GetDirectoryName(targetFile);
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            string metadataStr = null;

            if (ourMetadata.Value) { 
                metadataStr = "lfs|2|author:" + GetPhotographerMeta() + "|world:" + GetWorldMeta() + "|players:" + GetPlayerList(camera);
            }

            await EncodeAndSavePicture(targetFile, data, w, h, hasAlpha, metadataStr).ConfigureAwait(false);
        }
        
        private static unsafe (IntPtr, int) ToBytes(IntPtr pointer, int length)
        {
            var data = Marshal.AllocHGlobal(length);
            
            Buffer.MemoryCopy((void*) pointer, (void*)data, length, length);

            return (data, length);
        }
        
        private static ImageCodecInfo GetEncoder(ImageFormat format)  
        {  
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();  
            foreach (ImageCodecInfo codec in codecs)  
            {  
                if (codec.FormatID == format.Guid)  
                {  
                    return codec;  
                }  
            }  
            return null;  
        }  

        private static async Task EncodeAndSavePicture(string filePath, (IntPtr, int Length) pixelsPair, int w, int h, bool hasAlpha, string description)
        {
            if (pixelsPair.Item1 == IntPtr.Zero) return;
            
            // yield to background thread
            await Task.Delay(1).ConfigureAwait(false);
            
            if (Thread.CurrentThread == ourMainThread)
                MelonLogger.Error("Image encode is executed on main thread - it's a bug!");

            var step = hasAlpha ? 4 : 3;
            unsafe
            {
                // swap colors [a]rgb -> bgr[a]
                byte* pixels = (byte*) pixelsPair.Item1;
                for (int i = 0; i < pixelsPair.Length; i += step)
                {
                    var t = pixels[i];
                    pixels[i] = pixels[i + step - 1];
                    pixels[i + step - 1] = t;
                    if (step != 4) continue;

                    t = pixels[i + 1];
                    pixels[i + 1] = pixels[i + step - 2];
                    pixels[i + step - 2] = t;
                }

                // flip image upside-down
                for (var y = 0; y < h / 2; y++)
                {
                    for (var x = 0; x < w * step; x++)
                    {
                        var t = pixels[x + y * w * step];
                        pixels[x + y * w * step] = pixels[x + (h - y - 1) * w * step];
                        pixels[x + (h - y - 1) * w * step] = t;
                    }
                }
            }


            var pixelFormat = hasAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb;
            var bitmap = new Bitmap(w, h, pixelFormat);
            var bitmapData = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, pixelFormat);
            unsafe
            {
                Buffer.MemoryCopy((void*) pixelsPair.Item1, (void*) bitmapData.Scan0, pixelsPair.Length, pixelsPair.Length);
            }

            bitmap.UnlockBits(bitmapData);
            Marshal.FreeHGlobal(pixelsPair.Item1);

            //https://docs.microsoft.com/en-us/windows/win32/gdiplus/-gdiplus-constant-property-item-descriptions

            if (description != null) { 
                var pi = (PropertyItem)FormatterServices.GetUninitializedObject(typeof(PropertyItem));
                pi.Type = 2;
                pi.Id = 0x010E;  //PropertyTagImageDescription
                pi.Value = Encoding.Convert(Encoding.Unicode, Encoding.UTF8, Encoding.Unicode.GetBytes(description));
                pi.Len = pi.Value.Length;
                bitmap.SetPropertyItem(pi);
            }

            ImageCodecInfo encoder;
            EncoderParameters parameters;
            
            if (ourFormat.Value == "jpeg")
            {
                encoder = GetEncoder(ImageFormat.Jpeg);
                parameters = new EncoderParameters(1)
                {
                    Param = {[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, ourJpegPercent.Value)}
                };
                filePath = Path.ChangeExtension(filePath, ".jpeg");
                bitmap.Save(filePath, encoder, parameters);
            }
            else
                bitmap.Save(filePath, ImageFormat.Png);

            await ourToMainThread.Yield();

            MelonLogger.Msg($"Image saved to {filePath}");

            // compatibility with log-reading tools
            UnityEngine.Debug.Log($"Took screenshot to: {filePath}");
        }

        private static Func<int, int, string> ourOurGetPathMethod;
        
        static string GetPath(int w, int h)
        {
            ourOurGetPathMethod ??= (Func<int, int, string>) Delegate.CreateDelegate(typeof(Func<int, int, string>),
                typeof(CameraUtil)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Single(it =>
                        it.Name.StartsWith("Method_Private_Static_String_Int32_Int32_") && XrefScanner.XrefScan(it)
                            .Any(jt => jt.Type == XrefType.Global &&
                                       "yyyy-MM-dd_HH-mm-ss.fff" == jt.ReadAsObject()?.ToString())));

            return ourOurGetPathMethod(w, h);
        }
    }
}