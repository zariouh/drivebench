plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
    namespace = "com.drivebench.controller"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.drivebench.controller"
        minSdk = 26
        targetSdk = 35
        versionCode = 9
        versionName = "0.3.6"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures { buildConfig = true }
    signingConfigs.getByName("debug") {
        System.getenv("DRIVEBENCH_PREVIEW_KEYSTORE")?.let { storeFile=file(it) }
    }
    defaultConfig { manifestPlaceholders["appLabel"]="DriveBench" }
    buildTypes {
        getByName("debug") {
            applicationIdSuffix=".preview"
            manifestPlaceholders["appLabel"]="DriveBench Preview"
        }
        getByName("release") { isMinifyEnabled=false }
    }
}
kotlin { jvmToolchain(17) }
dependencies {
    implementation("androidx.activity:activity:1.10.1")
    implementation("androidx.camera:camera-camera2:1.4.2")
    implementation("androidx.camera:camera-lifecycle:1.4.2")
    implementation("androidx.camera:camera-view:1.4.2")
    implementation("com.google.zxing:core:3.5.4")
    testImplementation("junit:junit:4.13.2")
}
