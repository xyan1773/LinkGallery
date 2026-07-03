package com.linkgallery.companion.ui

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay

@Composable
fun PermissionScreen(
    connectionGuide: ConnectionGuide,
    onOpenPairingWindow: () -> Long = { 0L },
    activePairingCodeProvider: () -> String? = { null },
) {
    var permissionGranted by remember { mutableStateOf(false) }
    var pairingExpiresAt by remember { mutableStateOf<Long?>(null) }
    var pairingCode by remember { mutableStateOf<String?>(null) }
    val permissions = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        arrayOf(
            Manifest.permission.READ_MEDIA_IMAGES,
            Manifest.permission.READ_MEDIA_VIDEO,
        )
    } else {
        arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }
    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { results ->
        permissionGranted = results.values.all { it }
    }

    LaunchedEffect(pairingExpiresAt) {
        while (pairingExpiresAt?.let { System.currentTimeMillis() < it } == true) {
            pairingCode = activePairingCodeProvider()
            delay(1_000)
        }
        if (pairingExpiresAt?.let { System.currentTimeMillis() >= it } == true) {
            pairingExpiresAt = null
            pairingCode = null
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            text = if (permissionGranted) {
                "Photo and video read access is ready"
            } else {
                "Allow LinkGallery to read photos and videos"
            },
            style = MaterialTheme.typography.headlineSmall,
        )
        if (!permissionGranted) {
            Button(
                modifier = Modifier.padding(top = 24.dp),
                onClick = { launcher.launch(permissions) },
            ) {
                Text("Grant read access")
            }
        }
        Button(
            modifier = Modifier.padding(top = 24.dp),
            onClick = {
                pairingExpiresAt = onOpenPairingWindow()
                pairingCode = activePairingCodeProvider()
            },
        ) {
            Text("Add computer")
        }
        pairingExpiresAt?.let { expiresAt ->
            Text(
                modifier = Modifier.padding(top = 12.dp),
                text = "Pairing open until $expiresAt",
                style = MaterialTheme.typography.bodyMedium,
            )
            Text(
                modifier = Modifier.padding(top = 8.dp),
                text = pairingCode?.let { "Code: $it" } ?: "Waiting for pairing request",
                style = MaterialTheme.typography.titleMedium,
            )
        }
        Text(
            modifier = Modifier.padding(top = 32.dp),
            text = connectionGuide.title,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            modifier = Modifier.padding(top = 8.dp),
            text = connectionGuide.address,
            style = MaterialTheme.typography.bodyLarge,
        )
        Text(
            modifier = Modifier.padding(top = 8.dp),
            text = connectionGuide.detail,
            style = MaterialTheme.typography.bodyMedium,
        )
    }
}
