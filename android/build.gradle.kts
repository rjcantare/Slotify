plugins {
    id("com.android.application") version "9.2.0" apply false
    // AGP 9.x bundles Kotlin support; do not also apply org.jetbrains.kotlin.android,
    // it conflicts on the 'kotlin' Gradle extension. Only the serialization compiler
    // plugin is layered on top here.
    id("org.jetbrains.kotlin.plugin.serialization") version "2.0.21" apply false
}
