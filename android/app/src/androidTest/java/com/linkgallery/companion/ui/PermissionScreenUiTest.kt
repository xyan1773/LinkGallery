package com.linkgallery.companion.ui

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.performClick
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
    fun connectionPageShowsPairingCodeSheet() {
        compose.setContent {
            PermissionContent(
                connectionGuide = emulatorGuide(),
                permissionGranted = true,
                onPermissionRequest = {},
            )
        }

        compose.onNodeWithTag("tab_connection").performClick()
        compose.onNodeWithTag("show_pairing_code").performClick()
        compose.onNodeWithTag("pairing_code")
            .assertIsDisplayed()
            .assertTextContains("428 913")
    }

    private fun emulatorGuide() = createConnectionGuide(
        isEmulator = true,
        lanAddresses = emptyList(),
    )
}
