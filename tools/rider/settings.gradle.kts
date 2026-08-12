// The IntelliJ Platform artifacts live in JetBrains' own repository, which has to be declared before
// any plugin resolution happens — hence here rather than in build.gradle.kts, and hence first: Gradle
// rejects a settings file whose `plugins` block precedes `pluginManagement`.
pluginManagement {
    repositories {
        gradlePluginPortal()
        maven("https://cache-redirector.jetbrains.com/plugins.gradle.org/m2")
    }
}

// The platform needs a Java 21 toolchain, which is older than the JDK most machines now run Gradle
// on. This downloads it rather than making that a documented prerequisite.
plugins {
    id("org.gradle.toolchains.foojay-resolver-convention") version "0.9.0"
}

rootProject.name = "zigote-rider"
