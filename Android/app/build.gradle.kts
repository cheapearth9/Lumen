plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Passed by CI: -PlumenVersionName=1.2.3 -PlumenVersionCode=45 -PlumenKeystore=/path -PlumenKeystorePass=...
val lumenVersionName: String = (project.findProperty("lumenVersionName") as String?) ?: "1.0"
val lumenVersionCode: Int = (project.findProperty("lumenVersionCode") as String?)?.toIntOrNull() ?: 1
val lumenKeystore: String? = project.findProperty("lumenKeystore") as String?
val lumenKeystorePass: String = (project.findProperty("lumenKeystorePass") as String?) ?: "android"
val lumenKeyAlias: String = (project.findProperty("lumenKeyAlias") as String?) ?: "lumen"

android {
    namespace = "com.lumen.client.android"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.lumen.client.android"
        minSdk = 24
        targetSdk = 34
        versionCode = lumenVersionCode
        versionName = lumenVersionName
    }

    signingConfigs {
        if (lumenKeystore != null) {
            create("lumen") {
                storeFile = file(lumenKeystore)
                storePassword = lumenKeystorePass
                keyAlias = lumenKeyAlias
                keyPassword = lumenKeystorePass
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (lumenKeystore != null) signingConfigs.getByName("lumen") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }
}
