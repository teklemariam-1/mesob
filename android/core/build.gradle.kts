import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

// Lab builds only: the development vendor key, read from ../lab/vendor/vendor.pub (made by
// `mesob-lab vendor-init`). Production cores have the vendor key compiled into libmesob.so and
// ignore this value.
val labVendorPub: String = (project.findProperty("mesob.labVendorPub") as String?)
    ?: rootProject.file("../lab/vendor/vendor.pub").takeIf { it.exists() }?.readText()?.trim()
    ?: ""
// SHA-256 of the release signing certificate; when set, apps refuse to run if re-signed.
val signingCertSha256: String = (project.findProperty("mesob.signingCertSha256") as String?) ?: ""

android {
    namespace = "com.mesob.core"
    compileSdk = 36
    defaultConfig {
        minSdk = 24
        buildConfigField("String", "LAB_VENDOR_PUB", "\"$labVendorPub\"")
        buildConfigField("String", "SIGNING_CERT_SHA256", "\"$signingCertSha256\"")
        consumerProguardFiles("consumer-rules.pro")
    }
    buildFeatures { buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { compilerOptions { jvmTarget.set(JvmTarget.JVM_17) } }
