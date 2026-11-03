Current matched candidate: Android Preview0.3.6/code9 + PC Receiver0.3.8, recovery capability2 and one-second NEUTRAL recovery eligibility. Original100ms source/Host cutoff unchanged. See ../RECOVERY_038.md.

Current candidate: **Preview0.3.5/code8**, matched with PC Receiver0.3.7. See `../WIRELESS_RECOVERY_037.md`. The existing Preview signer/application ID is retained; native-device acceptance remains pending. Historical build notes below remain reference.

# DriveBench Android 0.3.2 — detailed-error build

Physical 0.3.1 pairing still failed with a generic interruption. This build exposes the actual call/native exception and adds **Connect → Connection details → Copy**. It does not claim to fix the underlying connection cause. Keep PC Receiver 0.3.0, scan a fresh QR and return the error report. The installed version appears in the heading; reports are local, bounded and scrubbed. No new permission or transport/driver change is included.

Native landscape driving controls with QR-paired local Wi-Fi input. Patch 0.3.1 fixes selection when Wi-Fi is available but not the default data network, preserves pairing errors and disables disconnected Resume. Use with the matching **DriveBench Wi-Fi receiver 0.3.0**. This replaces the offline-only 0.2.0 preview; the PC/LFS baseline is accepted as working.

Read `WIFI-GETTING-STARTED.md` in the project root for installation, scan/approve/Resume and the two control modes. This APK uses the same development key as the delivered 0.2.0 APK. It requests Internet/network-state access for the local connection and camera permission only for the optional pairing scanner. AndroidX also defines an internal signature permission for non-exported receiver handling. No account, location, storage permission or Google Play services is required.

MainActivity and the custom control surface are native Kotlin. An isolated CameraX/ZXing screen scans the PC QR. The platform TLS socket verifies the exact scanned certificate fingerprint; full-state UDP is HMAC-authenticated. Pairing/reconnect credentials are memory-only in this preview. Background/focus loss clears input, and backgrounding closes the network session. Reconnect and Resume are deliberate.

Toolchain: JDK 17, Gradle 8.11.1, AGP 8.9.2, Kotlin 2.1.20, compile/target SDK 35, Build Tools 35.0.0, minimum API 26. The wrapper distribution checksum is pinned. CameraX 1.4.2 / Activity 1.10.1 are pinned to this SDK-compatible build; ZXing core is 3.5.4. Exact runtime dependency evidence is retained in verification.

From this directory: `./gradlew --no-daemon testDebugUnitTest lintDebug assembleDebug` (Windows: `gradlew.bat`). Install the SDK packages through sdkmanager and set ANDROID_HOME. Never distribute local.properties, caches, build outputs or signing keys in the source checkpoint. The APK is `app/build/outputs/apk/debug/app-debug.apk`.

Current report-build results are in `ANDROID_032_BUILD_VERIFICATION.md`; prior selection results are in `ANDROID_031_BUILD_VERIFICATION.md`; historical networking/Windows results are in `NETWORK_BUILD_VERIFICATION.md`; the prior `ANDROID_BUILD_VERIFICATION.md` records the historical 0.2.0 offline build. Physical phone execution/rendering/sensors and the new real Android-to-Windows session remain unobserved. No remote GitHub build is claimed. App source is MIT; APK third-party licenses/notices are under assets; Gradle wrapper notices accompany its scripts/JAR. Full SDK/JDK/Gradle distributions are not included.