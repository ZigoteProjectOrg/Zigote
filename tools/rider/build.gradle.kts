plugins {
    kotlin("jvm") version "2.1.20"
    id("org.jetbrains.intellij.platform") version "2.5.0"
}

group = "dev.zigote"
version = "0.2.0"

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform {
        // useInstaller = false: Rider is only distributed to this plugin as an archive, and the default
        // (the installer) is rejected outright.
        rider(providers.gradleProperty("riderVersion"), useInstaller = false)
        testFramework(org.jetbrains.intellij.platform.gradle.TestFrameworkType.Platform)
    }
    testImplementation(kotlin("test"))

    // BasePlatformTestCase is a JUnit 3 TestCase. Without the vintage engine the JUnit 5 launcher does
    // not fail on it — it silently runs nothing, which is how a "passing" build shipped a broken panel.
    testImplementation("junit:junit:4.13.2")
    testRuntimeOnly("org.junit.vintage:junit-vintage-engine:5.10.2")
}

kotlin { jvmToolchain(21) }

intellijPlatform {
    pluginConfiguration {
        ideaVersion {
            sinceBuild = providers.gradleProperty("sinceBuild")
            untilBuild = provider { null }
        }
    }
}

tasks.test { useJUnitPlatform() }
