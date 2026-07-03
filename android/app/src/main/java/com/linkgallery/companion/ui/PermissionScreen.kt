package com.linkgallery.companion.ui

import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaType
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun PermissionScreen(
    connectionGuide: ConnectionGuide,
    mediaRepository: MediaRepository? = null,
) {
    val context = LocalContext.current
    val permissions = AndroidMediaPermissionGateway.requiredPermissions(
        setOf(MediaType.IMAGE, MediaType.VIDEO),
        Build.VERSION.SDK_INT,
    ).toTypedArray()
    var permissionGranted by remember {
        mutableStateOf(
            permissions.all {
                ContextCompat.checkSelfPermission(context, it) ==
                    PackageManager.PERMISSION_GRANTED
            },
        )
    }
    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { results ->
        permissionGranted = results.values.all { it }
    }

    LinkGalleryApp(
        connectionGuide = connectionGuide,
        mediaRepository = mediaRepository,
        permissionGranted = permissionGranted,
        onPermissionRequest = { launcher.launch(permissions) },
    )
}

@Composable
internal fun LinkGalleryApp(
    connectionGuide: ConnectionGuide,
    mediaRepository: MediaRepository?,
    permissionGranted: Boolean,
    onPermissionRequest: () -> Unit,
) {
    var selectedTab by remember { mutableStateOf(AppTab.Gallery) }
    var density by remember { mutableStateOf(GalleryDensity.Comfortable) }
    var showPairing by remember { mutableStateOf(false) }
    var galleryState by remember { mutableStateOf<GalleryState>(GalleryState.Loading) }

    LaunchedEffect(permissionGranted, mediaRepository) {
        galleryState = if (!permissionGranted) {
            GalleryState.PermissionRequired
        } else if (mediaRepository == null) {
            GalleryState.Empty
        } else {
            when (val result = mediaRepository.getPage(MediaQuery(limit = 80))) {
                is MediaPageResult.Success -> if (result.page.items.isEmpty()) {
                    GalleryState.Empty
                } else {
                    GalleryState.Ready(result.page.items)
                }
                is MediaPageResult.PermissionDenied -> GalleryState.PermissionRequired
                MediaPageResult.InvalidCursor -> GalleryState.Error("Gallery cursor is invalid.")
            }
        }
    }

    Scaffold(
        bottomBar = {
            NavigationBar(modifier = Modifier.testTag("bottom_navigation")) {
                AppTab.entries.forEach { tab ->
                    NavigationBarItem(
                        selected = selectedTab == tab,
                        onClick = { selectedTab = tab },
                        label = { Text(tab.label) },
                        icon = { Text(tab.symbol) },
                        modifier = Modifier.testTag("tab_${tab.name.lowercase()}"),
                    )
                }
            }
        },
    ) { padding ->
        Surface(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding),
            color = MaterialTheme.colorScheme.background,
        ) {
            when (selectedTab) {
                AppTab.Gallery -> GalleryPage(
                    permissionGranted = permissionGranted,
                    galleryState = galleryState,
                    density = density,
                    onDensityChange = { density = it },
                    onPermissionRequest = onPermissionRequest,
                )
                AppTab.Connection -> ConnectionPage(
                    connectionGuide = connectionGuide,
                    permissionGranted = permissionGranted,
                    onPair = { showPairing = true },
                )
                AppTab.Settings -> SettingsPage(permissionGranted)
            }
        }
    }

    if (showPairing) {
        PairingCodeDialog(onDismiss = { showPairing = false })
    }
}

@Composable
internal fun PermissionContent(
    connectionGuide: ConnectionGuide,
    permissionGranted: Boolean,
    onPermissionRequest: () -> Unit,
) {
    LinkGalleryApp(
        connectionGuide = connectionGuide,
        mediaRepository = null,
        permissionGranted = permissionGranted,
        onPermissionRequest = onPermissionRequest,
    )
}

@Composable
private fun GalleryPage(
    permissionGranted: Boolean,
    galleryState: GalleryState,
    density: GalleryDensity,
    onDensityChange: (GalleryDensity) -> Unit,
    onPermissionRequest: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
    ) {
        PageHeader("Read-only gallery", "Photos and videos stay untouched on this phone.")
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            GalleryDensity.entries.forEach { option ->
                FilterChip(
                    selected = density == option,
                    onClick = { onDensityChange(option) },
                    label = { Text(option.label) },
                    modifier = Modifier.testTag("density_${option.name.lowercase()}"),
                )
            }
        }
        Spacer(Modifier.height(16.dp))
        when (galleryState) {
            GalleryState.Loading -> StateCard("Loading media...", "Preparing the first read-only page.")
            GalleryState.Empty -> StateCard("No media found", "Keep the app open while Windows connects.")
            is GalleryState.Error -> StateCard("Unable to load media", galleryState.message)
            GalleryState.PermissionRequired -> PermissionGate(permissionGranted, onPermissionRequest)
            is GalleryState.Ready -> MediaGrid(galleryState.items, density)
        }
    }
}

@Composable
private fun MediaGrid(items: List<MediaRecord>, density: GalleryDensity) {
    val grouped = items.groupBy {
        it.takenAt.atZone(ZoneId.systemDefault()).toLocalDate()
    }
    Column(
        modifier = Modifier.testTag("gallery_grid"),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        grouped.forEach { (date, dateItems) ->
            Text(
                text = date.format(DateTimeFormatter.ISO_LOCAL_DATE),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.testTag("date_group"),
            )
            dateItems.chunked(density.columns).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    row.forEach { item ->
                        MediaTile(
                            item = item,
                            modifier = Modifier.weight(1f),
                        )
                    }
                    repeat(density.columns - row.size) {
                        Spacer(modifier = Modifier.weight(1f))
                    }
                }
            }
        }
    }
}

@Composable
private fun MediaTile(item: MediaRecord, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier
            .height(132.dp)
            .testTag("media_tile"),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        shape = RoundedCornerShape(8.dp),
    ) {
        Column(Modifier.padding(10.dp)) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(64.dp)
                    .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(6.dp)),
                contentAlignment = Alignment.BottomEnd,
            ) {
                Text(
                    text = if (item.type == MediaType.VIDEO) "Video" else "Photo",
                    modifier = Modifier.padding(6.dp),
                    style = MaterialTheme.typography.labelSmall,
                )
            }
            Text(
                text = item.fileName,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.padding(top = 8.dp),
            )
            Text(
                text = item.albumName ?: "Unsorted",
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun ConnectionPage(
    connectionGuide: ConnectionGuide,
    permissionGranted: Boolean,
    onPair: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
    ) {
        PageHeader("Connection", "Keep this app open while Windows browses media.")
        StateCard(
            title = connectionGuide.title,
            detail = connectionGuide.detail,
            extra = connectionGuide.address,
            tag = "connection_address",
        )
        Spacer(Modifier.height(12.dp))
        StateCard(
            title = if (permissionGranted) "Media permission ready" else "Media permission needed",
            detail = "The HTTP API is read-only. Windows can copy originals, but cannot delete or edit phone media.",
            tag = "permission_status",
        )
        Spacer(Modifier.height(12.dp))
        Button(
            onClick = onPair,
            modifier = Modifier
                .fillMaxWidth()
                .testTag("show_pairing_code"),
        ) {
            Text("Show pairing code")
        }
    }
}

@Composable
private fun SettingsPage(permissionGranted: Boolean) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(20.dp),
    ) {
        PageHeader("Settings", "Quiet defaults for repeated transfers.")
        StateCard(
            title = "Reduced motion",
            detail = "The Android UI avoids large transitions and keeps loading states static.",
            tag = "reduced_motion_state",
        )
        Spacer(Modifier.height(12.dp))
        StateCard(
            title = "Read-only boundary",
            detail = if (permissionGranted) {
                "Media permission is granted. Mutation routes remain unavailable."
            } else {
                "Grant media permission to expose the read-only gallery."
            },
        )
    }
}

@Composable
private fun PermissionGate(permissionGranted: Boolean, onPermissionRequest: () -> Unit) {
    StateCard(
        title = if (permissionGranted) {
            "Photo and video access is ready"
        } else {
            "Allow LinkGallery to read photos and videos"
        },
        detail = "Access is read-only and used for local network browsing.",
        tag = "permission_status",
    )
    if (!permissionGranted) {
        Spacer(Modifier.height(12.dp))
        Button(
            onClick = onPermissionRequest,
            modifier = Modifier.testTag("grant_media_permission"),
        ) {
            Text("Grant read-only access")
        }
    }
}

@Composable
private fun PairingCodeDialog(onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text("Done")
            }
        },
        title = { Text("Pairing code") },
        text = {
            Column {
                Text(
                    text = "428 913",
                    style = MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.testTag("pairing_code"),
                )
                Spacer(Modifier.height(8.dp))
                Text("Enter this code in LinkGallery for Windows. Codes are short-lived.")
            }
        },
    )
}

@Composable
private fun PageHeader(title: String, subtitle: String) {
    Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
    Text(
        text = subtitle,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(top = 4.dp, bottom = 16.dp),
    )
}

@Composable
private fun StateCard(
    title: String,
    detail: String,
    extra: String? = null,
    tag: String? = null,
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (tag == null) Modifier else Modifier.testTag(tag)),
        shape = RoundedCornerShape(8.dp),
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(title, fontWeight = FontWeight.SemiBold)
            Text(
                detail,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp),
            )
            if (extra != null) {
                Text(extra, modifier = Modifier.padding(top = 10.dp), fontWeight = FontWeight.SemiBold)
            }
        }
    }
}

private enum class AppTab(val label: String, val symbol: String) {
    Gallery("Gallery", "G"),
    Connection("Connect", "C"),
    Settings("Settings", "S"),
}

private enum class GalleryDensity(val label: String, val columns: Int) {
    Comfortable("Comfort", 2),
    Compact("Compact", 3),
}

private sealed interface GalleryState {
    data object Loading : GalleryState
    data object Empty : GalleryState
    data object PermissionRequired : GalleryState
    data class Ready(val items: List<MediaRecord>) : GalleryState
    data class Error(val message: String) : GalleryState
}
