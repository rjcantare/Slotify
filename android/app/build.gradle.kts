plugins {
    id("com.android.application")
    // Kotlin Android is bundled with AGP 9.x — no explicit plugin needed.
    id("org.jetbrains.kotlin.plugin.serialization")
}

android {
    namespace = "com.rentalsystem.android"
    compileSdk = 36

    signingConfigs {
        // The shipped release APK is signed with a private key that is NOT in
        // this repository. To sign your own builds consistently, put your own
        // keystore at app/debug.keystore (gitignored) or point SLOTIFY_KEYSTORE
        // at it. Without one, Gradle falls back to ~/.android/debug.keystore.
        getByName("debug") {
            val ks = file(System.getenv("SLOTIFY_KEYSTORE") ?: "debug.keystore")
            if (ks.exists()) {
                storeFile = ks
                storePassword = System.getenv("SLOTIFY_KEYSTORE_PASSWORD") ?: "android"
                keyAlias = System.getenv("SLOTIFY_KEY_ALIAS") ?: "androiddebugkey"
                keyPassword = System.getenv("SLOTIFY_KEY_PASSWORD") ?: "android"
            }
        }
    }

    defaultConfig {
        applicationId = "com.rentalsystem.android"
        minSdk = 26
        targetSdk = 36
        // versionCode / versionName can be overridden from the publish-update
        // script via `-PversionCode=NNN -PversionName=X.Y.Z` so the released
        // APK self-reports its semver. The defaults below are the local-dev
        // baseline used when no -P override is passed.
        versionCode = (project.findProperty("versionCode") as String?)?.toIntOrNull() ?: 3
        versionName = (project.findProperty("versionName") as String?) ?: "1.1.0"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        resources {
            // HiveMQ MQTT Client transitively bundles 6 Netty jars, each with its own
            // META-INF/INDEX.LIST + properties files. Android's mergeJavaResource step
            // does not know how to combine them. Drop the metadata files — they are
            // not needed at runtime.
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/io.netty.versions.properties",
                "META-INF/DEPENDENCIES",
                "META-INF/LICENSE.md",
                "META-INF/LICENSE.txt",
                "META-INF/NOTICE.md",
                "META-INF/NOTICE.txt",
                "META-INF/*.kotlin_module"
            )
        }
    }
}

// AGP 9.x bundles Kotlin but does not expose the legacy `kotlinOptions { jvmTarget }`
// extension on the android {} block. Pin the JVM bytecode target via the modern
// compiler-options DSL on all KotlinCompile tasks. This matches Java target above.
tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    // MQTT 3.1.1 client. Matches the broker QoS/retain/LWT contract the server and
    // Windows client speak. HiveMQ is the modern, maintained Android-friendly choice;
    // the deprecated Paho Android Service is not used.
    implementation("com.hivemq:hivemq-mqtt-client:1.3.5")

    // JSON for MQTT payload DTOs. kotlinx.serialization mirrors the C# DTOs
    // (PropertyNameCaseInsensitive on the wire; explicit @SerialName here).
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")

    // Coroutines so MQTT connect/publish can run off the UI thread under
    // lifecycleScope, matching the Activity lifecycle.
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")

    // S50b OTA: FileProvider lives in androidx.core. Declared explicitly so
    // the OTA install Intent does not depend on lifecycle's transitive pull.
    implementation("androidx.core:core-ktx:1.13.1")

    testImplementation("junit:junit:4.13.2")
}
