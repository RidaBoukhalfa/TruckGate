pluginManagement {
    repositories {
        gradlePluginPortal()
        mavenCentral()
        google()
    }
}

dependencyResolutionManagement {
    repositories {
        mavenCentral()
        google()
    }
}

rootProject.name = "truckgate-quarry"

// Pure Kotlin/JVM: ticket encoding, signing abstraction, verification. No Android dependency,
// so the Android app module (step 3) can depend on it and it runs on any JVM for tests.
include(":ticket-format")
