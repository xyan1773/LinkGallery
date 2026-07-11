package com.linkgallery.companion.ui

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import com.linkgallery.companion.LinkGalleryServiceState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class PermissionScreenUiTest {
    @get:Rule
    val compose = createComposeRule()

    @Test
    fun deniedPermissionExposesStableAutomationTagsAndRequestAction() {
        var requested = false
        compose.setContent {
            PermissionContent(
                connectionGuide = emulatorGuide(),
                permissionGranted = false,
                onPermissionRequest = { requested = true },
            )
        }

        compose.onNodeWithTag("grant_media_permission")
            .assertIsDisplayed()
            .performClick()
        compose.onNodeWithTag("tab_connection").performClick()
        compose.onNodeWithText("127.0.0.1:39570", substring = true)
            .assertIsDisplayed()

        assertTrue(requested)
    }

    @Test
    fun grantedPermissionShowsReadyStateWithoutGrantButton() {
        compose.setContent {
            PermissionContent(
                connectionGuide = emulatorGuide(),
                permissionGranted = true,
                onPermissionRequest = {},
            )
        }

        compose.onNodeWithTag("tab_albums")
            .assertIsDisplayed()
        compose.onNodeWithTag("tab_connection")
            .assertIsDisplayed()
        compose.onAllNodesWithTag("grant_media_permission").assertCountEquals(0)
    }

    @Test
    fun connectionPageDisplaysAddressCodeAndEnablesPairing() {
        var submittedCode: String? = null
        compose.setContent {
            PermissionContent(
                connectionGuide = emulatorGuide(),
                permissionGranted = true,
                onPermissionRequest = {},
                onOpenPairingWindow = { code ->
                    submittedCode = code
                    0L
                },
                serviceState = LinkGalleryServiceState(
                    running = true,
                    port = 39570,
                    addresses = listOf("172.23.45.108"),
                ),
            )
        }

        compose.onNodeWithTag("tab_connection").performClick()
        compose.onNodeWithTag("address_code")
            .assertIsDisplayed()
        compose.onNodeWithText("AC17-2D6C").assertIsDisplayed()
        compose.onNodeWithTag("enable_address_pairing").performClick()
        compose.runOnIdle { assertEquals("AC172D6C", submittedCode) }
    }

    private fun emulatorGuide() = createConnectionGuide(
        isEmulator = true,
        lanAddresses = emptyList(),
    )
}
