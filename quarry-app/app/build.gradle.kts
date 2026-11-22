plugins {
    id("com.android.application")
    kotlin("android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "dz.truckgate.quarry"
    compileSdk = 35

    defaultConfig {
        applicationId = "dz.truckgate.quarry"
        // Android 8.0+: java.time / java.util.Base64, per-use fingerprint keys (see DeviceKeys).
        minSdk = 26
        targetSdk = 35
        versionCode = 102
        versionName = "1.0.2"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    // API-level mistakes (a call that does not exist on the oldest supported Android) fail the build.
    lint {
        checkOnly += setOf("NewApi", "InlinedApi")
        checkDependencies = true
        abortOnError = true
        textReport = true
    }

    // Release signing comes only from the build environment (CI secrets), never from the repository:
    // TRUCKGATE_KEYSTORE (path), TRUCKGATE_KEYSTORE_PASSWORD, TRUCKGATE_KEY_ALIAS, TRUCKGATE_KEY_PASSWORD.
    val keystore = System.getenv("TRUCKGATE_KEYSTORE")?.takeIf { it.isNotBlank() && file(it).exists() }
    signingConfigs {
        if (keystore != null) {
            create("release") {
                storeFile = file(keystore)
                storePassword = System.getenv("TRUCKGATE_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("TRUCKGATE_KEY_ALIAS")
                keyPassword = System.getenv("TRUCKGATE_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (keystore != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    implementation(project(":quarry-core"))

    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.fragment:fragment-ktx:1.8.5")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("androidx.biometric:biometric:1.1.0")
    implementation("androidx.camera:camera-camera2:1.4.1")
    implementation("androidx.camera:camera-lifecycle:1.4.1")
    implementation("androidx.camera:camera-view:1.4.1")
    implementation("com.google.zxing:core:3.5.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("androidx.work:work-runtime-ktx:2.9.1")

    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
