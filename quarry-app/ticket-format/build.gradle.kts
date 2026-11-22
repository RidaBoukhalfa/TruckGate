plugins {
    kotlin("jvm")
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    testImplementation(platform("org.junit:junit-bom:5.10.2"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
    systemProperty("truckgate.vectors", rootDir.resolve("../ticket-spec/test-vectors").canonicalPath)
    testLogging { events("passed", "failed"); showStandardStreams = true }
}
