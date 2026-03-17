plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace  = "com.darci.app"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.darci.app"
        minSdk        = 26
        targetSdk     = 34
        versionCode   = 1
        versionName   = "1.0"

        // ── Local DARCI API (optional — used only when on home network) ──
        buildConfigField("String", "DARCI_BASE_URL",
            "\"${project.findProperty("DARCI_BASE_URL") ?: "http://10.0.2.2:5081"}\"")

        // ── AWS credentials (set these in local.properties — never commit them) ──
        buildConfigField("String", "DARCI_AWS_KEY_ID",
            "\"${project.findProperty("DARCI_AWS_KEY_ID") ?: ""}\"")
        buildConfigField("String", "DARCI_AWS_KEY_SECRET",
            "\"${project.findProperty("DARCI_AWS_KEY_SECRET") ?: ""}\"")
        buildConfigField("String", "DARCI_AWS_REGION",
            "\"${project.findProperty("DARCI_AWS_REGION") ?: "us-east-1"}\"")
        buildConfigField("String", "DARCI_SQS_INBOX",
            "\"${project.findProperty("DARCI_SQS_INBOX") ?: ""}\"")
        buildConfigField("String", "DARCI_SQS_OUTBOX",
            "\"${project.findProperty("DARCI_SQS_OUTBOX") ?: ""}\"")
        buildConfigField("String", "DARCI_S3_BUCKET",
            "\"${project.findProperty("DARCI_S3_BUCKET") ?: ""}\"")
        buildConfigField("Boolean", "DARCI_AWS_ENABLED",
            "${project.findProperty("DARCI_AWS_ENABLED") ?: "false"}")
    }

    buildFeatures {
        compose     = true
        buildConfig = true
    }

    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.8"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime)
    implementation(libs.androidx.lifecycle.viewmodel)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.navigation.compose)

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.ui)
    implementation(libs.androidx.ui.graphics)
    implementation(libs.androidx.ui.tooling.preview)
    implementation(libs.androidx.material3)
    debugImplementation(libs.androidx.ui.tooling)

    implementation(libs.retrofit)
    implementation(libs.retrofit.gson)
    implementation(libs.okhttp)
    implementation(libs.okhttp.logging)
    implementation(libs.gson)

    implementation(libs.aws.sqs)
    implementation(libs.aws.s3)

    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.coil.compose)
}
