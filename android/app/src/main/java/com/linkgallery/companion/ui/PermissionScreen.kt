package com.linkgallery.companion.ui

import android.content.Context
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.os.Build
import android.util.LruCache
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.linkgallery.companion.LinkGalleryServiceState
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaType
import com.linkgallery.companion.pairing.Ipv4AddressCode
import java.text.NumberFormat
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.LinkedHashMap
import java.util.Locale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext

private val LgEase = CubicBezierEasing(0.2f, 0.8f, 0.2f, 1f)
private val ThumbnailLoadGate = Semaphore(6)
private val ThumbnailMemoryCache = object : LruCache<String, ImageBitmap>(32 * 1024) {
    override fun sizeOf(key: String, value: ImageBitmap): Int =
        (value.width * value.height * 4 / 1024).coerceAtLeast(1)
}
private class AlbumPageCache {
    private val pages = object : LinkedHashMap<String, List<MediaRecord>>(16, 0.75f, true) {
        override fun removeEldestEntry(
            eldest: MutableMap.MutableEntry<String, List<MediaRecord>>?,
        ): Boolean = size > 12
    }

    operator fun get(key: String): List<MediaRecord>? = pages[key]

    operator fun set(key: String, items: List<MediaRecord>) {
        pages[key] = items
    }
}

@Composable
fun PermissionScreen(
    connectionGuide: ConnectionGuide,
    mediaRepository: MediaRepository? = null,
    serviceState: LinkGalleryServiceState = LinkGalleryServiceState(running = true),
    onServiceRunningChange: (Boolean) -> Unit = {},
    onOpenPairingWindow: (String?) -> Long = { 0L },
) {
    val context = LocalContext.current
    val permissions = AndroidMediaPermissionGateway.requiredPermissions(
        setOf(MediaType.IMAGE, MediaType.VIDEO),
        Build.VERSION.SDK_INT,
    ).toTypedArray()
    var permissionGranted by remember {
        mutableStateOf(
            permissions.all {
                ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
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
        serviceState = serviceState,
        onServiceRunningChange = onServiceRunningChange,
        onPermissionRequest = { launcher.launch(permissions) },
        onOpenPairingWindow = onOpenPairingWindow,
    )
}

@Composable
internal fun LinkGalleryApp(
    connectionGuide: ConnectionGuide,
    mediaRepository: MediaRepository?,
    permissionGranted: Boolean,
    onPermissionRequest: () -> Unit,
    serviceState: LinkGalleryServiceState = LinkGalleryServiceState(running = true),
    onServiceRunningChange: (Boolean) -> Unit = {},
    onOpenPairingWindow: (String?) -> Long = { 0L },
) {
    val context = LocalContext.current
    val preferences = remember { context.getSharedPreferences("linkgallery_preferences", Context.MODE_PRIVATE) }
    var language by remember {
        mutableStateOf(
            when (preferences.getString("language", UiLanguage.Chinese.name)) {
                UiLanguage.English.name -> UiLanguage.English
                else -> UiLanguage.Chinese
            },
        )
    }
    val strings = remember(language) { UiStrings(language) }
    var selectedTab by rememberSaveable { mutableStateOf(AppTab.Photos) }
    var galleryMode by rememberSaveable { mutableStateOf(GalleryMode.AllPhotos) }
    var gridColumns by rememberSaveable { mutableIntStateOf(4) }
    var selectedFilter by remember { mutableStateOf(MediaFilter.All) }
    var galleryState by remember { mutableStateOf<GalleryState>(GalleryState.Loading) }
    var toastMessage by remember { mutableStateOf<String?>(null) }
    val albumPageCache = remember(mediaRepository) { AlbumPageCache() }
    val photosGridState = rememberLazyGridState()
    val albumsScrollState = rememberScrollState()

    fun showToast(message: String) {
        toastMessage = message
    }

    fun setLanguage(next: UiLanguage) {
        language = next
        preferences.edit().putString("language", next.name).apply()
        showToast(UiStrings(next).t("Language updated", "语言已更新"))
    }

    LaunchedEffect(toastMessage) {
        if (toastMessage != null) {
            delay(1_800)
            toastMessage = null
        }
    }

    LaunchedEffect(permissionGranted, mediaRepository) {
        galleryState = if (!permissionGranted) {
            GalleryState.PermissionRequired
        } else if (mediaRepository == null) {
            GalleryState.Empty
        } else {
            when (val result = withContext(Dispatchers.IO) {
                mediaRepository.getPage(MediaQuery(limit = 200))
            }) {
                is MediaPageResult.Success -> if (result.page.items.isEmpty()) {
                    GalleryState.Empty
                } else {
                    GalleryState.Ready(result.page.items)
                }
                is MediaPageResult.PermissionDenied -> GalleryState.PermissionRequired
                MediaPageResult.InvalidCursor -> GalleryState.Error(strings.galleryCursorInvalid)
            }
        }
    }

    Scaffold(
        containerColor = LgParchment,
        bottomBar = {
            Surface(
                color = LgCanvas,
                shadowElevation = 0.dp,
                modifier = Modifier.testTag("bottom_navigation"),
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .navigationBarsPadding()
                        .height(72.dp)
                        .padding(horizontal = 10.dp, vertical = 10.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    AppTab.entries.filterNot { it == AppTab.Albums }.forEach { tab ->
                        val selected = selectedTab == tab
                        Column(
                            modifier = Modifier
                                .weight(1f)
                                .fillMaxHeight()
                                .clickable { selectedTab = tab }
                                .testTag(tab.testTag),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.Center,
                        ) {
                            Text(
                                text = tab.symbol,
                                color = if (selected) LgBlueStrong else LgMuted,
                                fontSize = 17.sp,
                                fontWeight = FontWeight.SemiBold,
                            )
                            Spacer(Modifier.height(2.dp))
                            Text(
                                text = tab.label(strings),
                                color = if (selected) LgBlueStrong else LgMuted,
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                            )
                        }
                    }
                }
            }
        },
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding),
        ) {
            Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
                when (selectedTab) {
                        AppTab.Photos -> Column(Modifier.fillMaxSize()) {
                            GalleryModeSelector(
                                selected = galleryMode,
                                onSelected = { galleryMode = it },
                                strings = strings,
                            )
                            Box(Modifier.weight(1f)) {
                                when (galleryMode) {
                                    GalleryMode.AllPhotos -> PhotosPage(
                                        galleryState = galleryState,
                                        selectedFilter = selectedFilter,
                                        mediaRepository = mediaRepository,
                                        onFilterChange = { selectedFilter = it },
                                        onPermissionRequest = onPermissionRequest,
                                        onToast = ::showToast,
                                        strings = strings,
                                        gridColumns = gridColumns,
                                        onGridColumnsChange = { gridColumns = it },
                                        gridState = photosGridState,
                                    )
                                    GalleryMode.Albums -> AlbumsPage(
                                        galleryState = galleryState,
                                        mediaRepository = mediaRepository,
                                        albumPageCache = albumPageCache,
                                        permissionGranted = permissionGranted,
                                        onPermissionRequest = onPermissionRequest,
                                        onManageDevices = { selectedTab = AppTab.Connection },
                                        onToast = ::showToast,
                                        strings = strings,
                                        gridColumns = gridColumns,
                                        onGridColumnsChange = { gridColumns = it },
                                        albumsScrollState = albumsScrollState,
                                    )
                                }
                            }
                        }
                        AppTab.Albums -> Unit
                        AppTab.Connection -> DevicesPage(
                            connectionGuide = connectionGuide,
                            serviceState = serviceState,
                            onServiceRunningChange = onServiceRunningChange,
                            permissionGranted = permissionGranted,
                            onPair = { code -> onOpenPairingWindow(code) },
                            onToast = ::showToast,
                            strings = strings,
                            language = language,
                            onLanguageChange = ::setLanguage,
                        )
                }
            }
            ToastOverlay(
                message = toastMessage,
                modifier = Modifier.align(Alignment.BottomCenter),
            )
        }
    }

}

@Composable
internal fun PermissionContent(
    connectionGuide: ConnectionGuide,
    permissionGranted: Boolean,
    onPermissionRequest: () -> Unit,
    onOpenPairingWindow: (String?) -> Long = { 0L },
    serviceState: LinkGalleryServiceState = LinkGalleryServiceState(running = true),
    onServiceRunningChange: (Boolean) -> Unit = {},
) {
    LinkGalleryApp(
        connectionGuide = connectionGuide,
        mediaRepository = null,
        permissionGranted = permissionGranted,
        onPermissionRequest = onPermissionRequest,
        onOpenPairingWindow = onOpenPairingWindow,
        serviceState = serviceState,
        onServiceRunningChange = onServiceRunningChange,
    )
}

@Composable
private fun GalleryModeSelector(
    selected: GalleryMode,
    onSelected: (GalleryMode) -> Unit,
    strings: UiStrings,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        GalleryMode.entries.forEach { mode ->
            FilterChip(
                selected = selected == mode,
                onClick = { onSelected(mode) },
                label = {
                    Text(if (mode == GalleryMode.AllPhotos) strings.allPhotos else strings.albums)
                },
                modifier = Modifier
                    .weight(1f)
                    .testTag(
                        if (mode == GalleryMode.AllPhotos) {
                            "gallery_mode_all_photos"
                        } else {
                            "gallery_mode_albums"
                        },
                    ),
                shape = RoundedCornerShape(999.dp),
            )
        }
    }
}

@Composable
private fun DensityChip(
    columns: Int,
    onColumnsChange: (Int) -> Unit,
    strings: UiStrings,
) {
    FilterChip(
        selected = false,
        onClick = { onColumnsChange(if (columns >= 5) 3 else columns + 1) },
        label = { Text(strings.gridColumns(columns)) },
        modifier = Modifier.testTag("grid_density"),
        shape = RoundedCornerShape(999.dp),
    )
}

@Composable
private fun AlbumsPage(
    galleryState: GalleryState,
    mediaRepository: MediaRepository?,
    albumPageCache: AlbumPageCache,
    permissionGranted: Boolean,
    onPermissionRequest: () -> Unit,
    onManageDevices: () -> Unit,
    onToast: (String) -> Unit,
    strings: UiStrings,
    gridColumns: Int,
    onGridColumnsChange: (Int) -> Unit,
    albumsScrollState: ScrollState,
) {
    val mediaItems = (galleryState as? GalleryState.Ready)?.items.orEmpty()
    val smartAlbums = remember(mediaItems, strings.uiLanguage) { buildSmartAlbums(mediaItems, strings) }
    val deviceAlbums = remember(mediaItems, strings.uiLanguage) { buildDeviceAlbums(mediaItems, strings) }
    val scope = rememberCoroutineScope()
    val libraryCacheVersion = remember(mediaItems) {
        val fingerprint = mediaItems.fold(1_125_899_906_842_597L) { value, item ->
            val generation = item.generation ?: item.modifiedAt.toEpochMilli()
            (value * 31 + item.id.hashCode()) * 31 + generation
        }
        "${mediaItems.size}:$fingerprint"
    }
    var activeAlbum by remember { mutableStateOf<AlbumUi?>(null) }
    var activeAlbumItems by remember { mutableStateOf<List<MediaRecord>>(emptyList()) }
    var albumLoading by remember { mutableStateOf(false) }
    var activeAlbumFilter by remember { mutableStateOf(MediaFilter.All) }
    val activeAlbumIdentity = activeAlbum?.let { album ->
        album.albumId ?: album.relativePath ?: album.name
    }
    val albumDetailGridState = remember(activeAlbumIdentity) { LazyGridState() }

    if (activeAlbum != null) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 16.dp, vertical = 14.dp),
        ) {
            TextButton(
                onClick = {
                    activeAlbum = null
                    activeAlbumItems = emptyList()
                    activeAlbumFilter = MediaFilter.All
                },
                contentPadding = PaddingValues(horizontal = 0.dp, vertical = 4.dp),
            ) {
                Text("\u2039 ${strings.albums}", color = LgBlue, fontWeight = FontWeight.SemiBold)
            }
            Text(
                activeAlbum!!.name,
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )
            Text(
                strings.itemCount(activeAlbumItems.size),
                color = LgMuted,
                style = MaterialTheme.typography.bodySmall,
            )
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                MediaFilter.entries.forEach { filter ->
                    PressScale(targetScale = 0.96f) { pressModifier ->
                        FilterChip(
                            selected = activeAlbumFilter == filter,
                            onClick = { activeAlbumFilter = filter },
                            label = { Text(filter.label(strings)) },
                            shape = RoundedCornerShape(999.dp),
                            modifier = pressModifier,
                        )
                    }
                }
                DensityChip(gridColumns, onGridColumnsChange, strings)
            }
            Spacer(Modifier.height(14.dp))
            if (albumLoading) {
                StateCard(strings.loadingMedia, strings.preparingFirstPage)
            } else if (activeAlbumItems.isEmpty()) {
                StateCard(strings.noMediaFound, strings.albumMayBeEmpty)
            } else {
                MediaGrid(
                    items = activeAlbumItems,
                    selectedFilter = activeAlbumFilter,
                    mediaRepository = mediaRepository,
                    selectionMode = false,
                    selectedIds = emptySet(),
                    onToggleSelection = {},
                    strings = strings,
                    columns = gridColumns,
                    gridState = albumDetailGridState,
                )
            }
        }
        return
    }

    fun openAlbum(album: AlbumUi) {
        val embeddedItems = album.members
        val albumIdentity = album.albumId ?: album.relativePath ?: album.name
        val cacheKey = "$albumIdentity:$libraryCacheVersion"
        if (embeddedItems != null) {
            activeAlbum = album
            activeAlbumItems = embeddedItems
            albumLoading = false
            return
        }
        if (album.albumId == null || mediaRepository == null) {
            onToast(strings.albumUnavailable)
            return
        }
        activeAlbum = album
        albumPageCache[cacheKey]?.let { cached ->
            activeAlbumItems = cached
            albumLoading = false
            return
        }
        albumLoading = true
        scope.launch {
            activeAlbumItems = when (val result = withContext(Dispatchers.IO) {
                mediaRepository.getPage(MediaQuery(limit = 200, albumId = album.albumId))
            }) {
                is MediaPageResult.Success -> result.page.items.also { items ->
                    albumPageCache[cacheKey] = items
                }
                else -> emptyList()
            }
            albumLoading = false
        }
    }
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(albumsScrollState)
            .padding(horizontal = 16.dp, vertical = 14.dp),
    ) {
        TopBar(
            title = strings.albums,
            subtitle = strings.itemCount(mediaItems.size),
            action = "",
            onAction = {},
            eyebrow = "LinkGallery",
        )
        if (!permissionGranted || galleryState is GalleryState.PermissionRequired) {
            PermissionGate(permissionGranted, onPermissionRequest, strings)
            Spacer(Modifier.height(18.dp))
        } else if (galleryState is GalleryState.Empty) {
            StateCard(strings.noMediaFound, strings.keepOpenForWindows)
            Spacer(Modifier.height(18.dp))
        }

        AlbumSection(
            strings.smartAlbums,
            null,
            smartAlbums,
            mediaRepository = mediaRepository,
            strings = strings,
            onAlbumClick = ::openAlbum,
        )
        Spacer(Modifier.height(20.dp))
        AlbumSection(
            title = strings.deviceAlbums,
            action = strings.manage,
            albums = deviceAlbums,
            mediaRepository = mediaRepository,
            onAction = onManageDevices,
            onAlbumClick = ::openAlbum,
            strings = strings,
        )
    }
}

@Composable
private fun PhotosPage(
    galleryState: GalleryState,
    selectedFilter: MediaFilter,
    mediaRepository: MediaRepository?,
    onFilterChange: (MediaFilter) -> Unit,
    onPermissionRequest: () -> Unit,
    onToast: (String) -> Unit,
    strings: UiStrings,
    gridColumns: Int,
    onGridColumnsChange: (Int) -> Unit,
    gridState: LazyGridState,
) {
    val mediaCount = (galleryState as? GalleryState.Ready)?.items?.size ?: 0
    var selectionMode by remember { mutableStateOf(false) }
    var selectedIds by remember { mutableStateOf<Set<String>>(emptySet()) }

    fun toggleSelection(id: String) {
        selectedIds = if (id in selectedIds) selectedIds - id else selectedIds + id
    }

    fun closeSelection() {
        selectedIds = emptySet()
        selectionMode = false
    }

    Box(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 12.dp, vertical = 10.dp),
        ) {
            TopBar(
                title = strings.photos,
                subtitle = strings.itemCount(mediaCount),
                action = if (selectionMode) strings.selectedCount(selectedIds.size) else strings.multiSelect,
                onAction = {
                    if (selectionMode) closeSelection() else selectionMode = true
                },
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                MediaFilter.entries.forEach { filter ->
                    PressScale(targetScale = 0.96f) { pressModifier ->
                        FilterChip(
                            selected = selectedFilter == filter,
                            onClick = { onFilterChange(filter) },
                            label = { Text(filter.label(strings)) },
                            shape = RoundedCornerShape(999.dp),
                            modifier = pressModifier,
                        )
                    }
                }
                DensityChip(gridColumns, onGridColumnsChange, strings)
            }
            Spacer(Modifier.height(14.dp))
            when (galleryState) {
                GalleryState.Loading -> StateCard(strings.loadingMedia, strings.preparingFirstPage)
                GalleryState.Empty -> StateCard(strings.noMediaFound, strings.keepOpenForWindows)
                is GalleryState.Error -> StateCard(strings.unableToLoadMedia, galleryState.message)
                GalleryState.PermissionRequired -> PermissionGate(false, onPermissionRequest, strings)
                is GalleryState.Ready -> MediaGrid(
                    items = galleryState.items,
                    selectedFilter = selectedFilter,
                    mediaRepository = mediaRepository,
                    selectionMode = selectionMode,
                    selectedIds = selectedIds,
                    onToggleSelection = ::toggleSelection,
                    strings = strings,
                    columns = gridColumns,
                    gridState = gridState,
                )
            }
        }
        AnimatedVisibility(
            visible = selectionMode && selectedIds.isNotEmpty(),
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(horizontal = 12.dp, vertical = 12.dp),
            enter = fadeIn(),
            exit = fadeOut(),
        ) {
            SelectionActionBar(
                selectedCount = selectedIds.size,
                onCopy = { onToast(strings.copyComplete) },
                strings = strings,
            )
        }
    }
}

@Composable
private fun DevicesPage(
    connectionGuide: ConnectionGuide,
    serviceState: LinkGalleryServiceState,
    onServiceRunningChange: (Boolean) -> Unit,
    permissionGranted: Boolean,
    onPair: (String) -> Unit,
    onToast: (String) -> Unit,
    strings: UiStrings,
    language: UiLanguage,
    onLanguageChange: (UiLanguage) -> Unit,
) {
    var showSettings by remember { mutableStateOf(false) }
    val ipv4Address = remember(serviceState.addresses) {
        serviceState.addresses.firstOrNull { Ipv4AddressCode.encode(it) != null }
    }
    val addressCode = remember(ipv4Address) { ipv4Address?.let(Ipv4AddressCode::encode) }
    val serviceAddress = remember(ipv4Address, serviceState.port, connectionGuide.address) {
        val address = ipv4Address
        val port = serviceState.port
        if (address != null && port != null) {
            "http://$address:$port/api/v1/"
        } else {
            connectionGuide.address
        }
    }
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        TopBar(
            title = strings.devices,
            subtitle = strings.wifiMediaService,
            action = if (showSettings) strings.done else strings.settings,
            onAction = { showSettings = !showSettings },
        )
        Card(
            colors = CardDefaults.cardColors(containerColor = LgBlue),
            shape = RoundedCornerShape(20.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Row(
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(
                    Modifier
                        .size(9.dp)
                        .clip(CircleShape)
                        .background(if (serviceState.running) LgSuccess else LgMuted),
                )
                Column(Modifier.padding(start = 10.dp).weight(1f)) {
                    Text(
                        text = if (serviceState.running) strings.serviceRunning else strings.serviceStopped,
                        color = Color.White,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(
                        text = serviceAddress,
                        color = Color.White.copy(alpha = 0.82f),
                        style = MaterialTheme.typography.bodySmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.testTag("connection_address"),
                    )
                }
                Text(
                    text = serviceState.port?.toString() ?: "—",
                    color = Color.White,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier
                        .background(Color.White.copy(alpha = 0.16f), RoundedCornerShape(10.dp))
                        .padding(horizontal = 10.dp, vertical = 6.dp),
                )
            }
        }
        Spacer(Modifier.height(12.dp))
        Card(
            colors = CardDefaults.cardColors(containerColor = LgCanvas),
            shape = RoundedCornerShape(18.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, LgLine, RoundedCornerShape(18.dp)),
        ) {
            Column(Modifier.padding(14.dp)) {
                Text(strings.connectComputer, fontWeight = FontWeight.SemiBold)
                Text(
                    strings.connectComputerDetail,
                    color = LgMuted,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 3.dp, bottom = 12.dp),
                )
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(LgParchment, RoundedCornerShape(14.dp))
                        .border(1.dp, LgLine, RoundedCornerShape(14.dp))
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
                        Text(strings.addressCode, color = LgMuted, style = MaterialTheme.typography.labelMedium)
                        Text(
                            text = addressCode?.let(Ipv4AddressCode::format) ?: "---- ----",
                            color = LgInk,
                            fontSize = 30.sp,
                            fontWeight = FontWeight.SemiBold,
                            letterSpacing = 2.sp,
                            modifier = Modifier.testTag("address_code"),
                        )
                        Text(
                            strings.addressCodeHelp,
                            color = LgMuted,
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 3.dp),
                        )
                    }
                }
                Button(
                    onClick = {
                        if (addressCode == null) {
                            onToast(strings.addressUnavailable)
                        } else {
                            onPair(addressCode)
                            onToast(strings.waitingForWindows)
                        }
                    },
                    enabled = serviceState.running && addressCode != null,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 10.dp)
                        .height(50.dp)
                        .testTag("enable_address_pairing"),
                    shape = RoundedCornerShape(14.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = LgBlueStrong),
                ) {
                    Text(strings.enablePairing)
                }
                if (serviceState.pairingCode != null) {
                    Text(
                        strings.pairingReady(serviceState.pairingCode),
                        color = LgBlueStrong,
                        style = MaterialTheme.typography.bodySmall,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier
                            .padding(top = 10.dp)
                            .testTag("pairing_ready"),
                    )
                }
            }
        }
        Spacer(Modifier.height(10.dp))
        SettingRow(
            title = strings.pairedComputer,
            detail = serviceState.pairedDesktopNames
                .takeIf { it.isNotEmpty() }
                ?.joinToString()
                ?: strings.noPairedDesktop,
            trailing = {
                Text(strings.pair, color = LgBlue, fontWeight = FontWeight.SemiBold)
            },
        )
        serviceState.transferStatus?.let { transfer ->
            Spacer(Modifier.height(10.dp))
            Card(
                colors = CardDefaults.cardColors(containerColor = LgCanvas),
                shape = RoundedCornerShape(18.dp),
                modifier = Modifier
                    .fillMaxWidth()
                    .border(1.dp, LgLine, RoundedCornerShape(18.dp))
                    .testTag("transfer_status_card"),
            ) {
                Column(Modifier.padding(14.dp)) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(strings.savingToComputer, fontWeight = FontWeight.SemiBold)
                            Text(
                                "${transfer.desktopName} · ${transfer.destinationName}",
                                color = LgMuted,
                                style = MaterialTheme.typography.bodySmall,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.testTag("transfer_destination"),
                            )
                        }
                        TextButton(
                            onClick = { onServiceRunningChange(false) },
                            modifier = Modifier.testTag("stop_sharing"),
                        ) {
                            Text(strings.stopSharing)
                        }
                    }
                    LinearProgressIndicator(
                        progress = { transfer.progress },
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 10.dp)
                            .testTag("transfer_progress"),
                        color = LgBlueStrong,
                        trackColor = LgBlueSoft,
                    )
                    Text(
                        strings.transferProgress(
                            transfer.completedItems,
                            transfer.totalItems,
                            transfer.progress,
                        ),
                        color = LgMuted,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(top = 6.dp),
                    )
                }
            }
        }
        AnimatedVisibility(visible = showSettings) {
            Column {
                SettingRow(
                    title = strings.keepServiceRunning,
                    detail = strings.allowPairedComputers,
                    trailing = {
                        LinkGallerySwitch(
                            checked = serviceState.running,
                            onCheckedChange = {
                                onServiceRunningChange(it)
                                onToast(if (it) strings.mediaServiceStarted else strings.mediaServiceStopped)
                            },
                        )
                    },
                )
                SettingRow(
                    title = strings.languageLabel,
                    detail = strings.chooseLanguage,
                    trailing = {
                        Row(horizontalArrangement = Arrangement.spacedBy(2.dp)) {
                            TextButton(onClick = { onLanguageChange(UiLanguage.Chinese) }) {
                                Text(
                                    text = "中文",
                                    color = if (language == UiLanguage.Chinese) LgBlue else LgMuted,
                                    fontWeight = FontWeight.SemiBold,
                                )
                            }
                            TextButton(onClick = { onLanguageChange(UiLanguage.English) }) {
                                Text(
                                    text = "EN",
                                    color = if (language == UiLanguage.English) LgBlue else LgMuted,
                                    fontWeight = FontWeight.SemiBold,
                                )
                            }
                        }
                    },
                )
                StateCard(
                    title = if (permissionGranted) strings.mediaPermissionReady else strings.mediaPermissionNeeded,
                    detail = strings.readOnlyApiDetail,
                    tag = "permission_status",
                )
            }
        }
    }
}

@Composable
private fun AlbumSection(
    title: String,
    action: String?,
    albums: List<AlbumUi>,
    mediaRepository: MediaRepository?,
    strings: UiStrings,
    emptyMessage: String? = null,
    onAction: () -> Unit = {},
    onAlbumClick: (AlbumUi) -> Unit = {},
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        if (action != null) {
            TextButton(onClick = onAction) { Text(action) }
        }
    }
    Spacer(Modifier.height(8.dp))
    if (albums.isEmpty()) {
        StateCard(emptyMessage ?: strings.noAlbumsYet, strings.albumsIndexedLater)
        return
    }
    LazyVerticalGrid(
        columns = GridCells.Fixed(2),
        modifier = Modifier.height(((albums.size + 1) / 2 * 166).dp),
        userScrollEnabled = false,
        contentPadding = PaddingValues(0.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        items(albums) { album ->
            AlbumCard(album, mediaRepository, strings, onAlbumClick)
        }
    }
}

@Composable
private fun AlbumCard(
    album: AlbumUi,
    mediaRepository: MediaRepository?,
    strings: UiStrings,
    onClick: (AlbumUi) -> Unit,
) {
    PressScale(targetScale = 0.98f, onClick = { onClick(album) }) { pressModifier ->
        Column(modifier = pressModifier) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .aspectRatio(1.65f)
                    .clip(RoundedCornerShape(13.dp))
                    .background(album.color),
                contentAlignment = Alignment.TopStart,
            ) {
                MediaThumbnailPreview(
                    item = album.cover,
                    mediaRepository = mediaRepository,
                    modifier = Modifier.fillMaxSize(),
                    fallbackText = album.name.take(1).uppercase(Locale.getDefault()),
                    fallbackBackground = album.color,
                )
                if (album.cover == null) {
                    Box(
                        modifier = Modifier
                            .padding(start = 12.dp, top = 12.dp)
                            .size(31.dp)
                            .clip(CircleShape)
                            .background(Color.White.copy(alpha = 0.58f)),
                    )
                    Box(
                        modifier = Modifier
                            .align(Alignment.BottomEnd)
                            .padding(end = 14.dp, bottom = 16.dp)
                            .size(width = 48.dp, height = 21.dp)
                            .clip(RoundedCornerShape(6.dp))
                            .background(LgInk.copy(alpha = 0.34f)),
                    )
                }
                if (album.tag != null) {
                    Text(
                        text = album.tag,
                        color = if (album.isSmart) LgBlue else Color.White,
                        style = MaterialTheme.typography.labelSmall,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier
                            .padding(start = 12.dp, top = 12.dp)
                            .background(
                                if (album.isSmart) Color.White.copy(alpha = 0.9f)
                                else Color.Black.copy(alpha = 0.35f),
                                RoundedCornerShape(999.dp),
                            )
                            .padding(horizontal = 8.dp, vertical = 3.dp),
                    )
                }
            }
            Text(
                text = album.name,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                fontWeight = FontWeight.SemiBold,
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(top = 8.dp),
            )
            Text(
                text = strings.itemCount(album.count),
                color = LgMuted,
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun MediaGrid(
    items: List<MediaRecord>,
    selectedFilter: MediaFilter,
    mediaRepository: MediaRepository?,
    selectionMode: Boolean,
    selectedIds: Set<String>,
    onToggleSelection: (String) -> Unit,
    strings: UiStrings,
    columns: Int,
    gridState: LazyGridState,
) {
    val filtered = remember(items, selectedFilter) {
        when (selectedFilter) {
            MediaFilter.All -> items
            MediaFilter.Photos -> items.filter { it.type == MediaType.IMAGE }
            MediaFilter.Videos -> items.filter { it.type == MediaType.VIDEO }
        }
    }
    val grouped = remember(filtered) {
        filtered.sortedByDescending { it.takenAt }.groupBy {
            it.takenAt.atZone(ZoneId.systemDefault()).toLocalDate()
        }
    }
    LazyVerticalGrid(
        columns = GridCells.Fixed(columns),
        state = gridState,
        modifier = Modifier
            .fillMaxSize()
            .testTag("gallery_grid"),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        grouped.forEach { (date, dateItems) ->
            item(span = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }) {
                Text(
                    text = date.format(DateTimeFormatter.ISO_LOCAL_DATE),
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(top = 8.dp, bottom = 4.dp),
                )
            }
            items(dateItems, key = { it.id }) { item ->
                MediaTile(
                    item = item,
                    mediaRepository = mediaRepository,
                    selectionMode = selectionMode,
                    selected = item.id in selectedIds,
                    onToggleSelection = onToggleSelection,
                    strings = strings,
                )
            }
        }
    }
}

@Composable
private fun MediaTile(
    item: MediaRecord,
    mediaRepository: MediaRepository?,
    selectionMode: Boolean,
    selected: Boolean,
    onToggleSelection: (String) -> Unit,
    strings: UiStrings,
) {
    val shape = RoundedCornerShape(10.dp)
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .clip(shape)
            .background(LgCanvas)
            .then(if (selected) Modifier.border(2.dp, LgBlue, shape) else Modifier)
            .clickable(enabled = selectionMode) { onToggleSelection(item.id) }
            .testTag("media_tile"),
        contentAlignment = Alignment.BottomEnd,
    ) {
        MediaThumbnailPreview(
            item = item,
            mediaRepository = mediaRepository,
            modifier = Modifier.fillMaxSize(),
            fallbackText = item.fileName.substringAfterLast('.', "Media")
                .take(5)
                .uppercase(Locale.getDefault()),
        )
        if (selectionMode) {
            Box(
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(7.dp)
                    .size(23.dp)
                    .clip(CircleShape)
                    .background(if (selected) LgBlueStrong else Color.White.copy(alpha = 0.88f))
                    .border(
                        width = 1.5.dp,
                        color = if (selected) Color.White else LgMuted,
                        shape = CircleShape,
                    ),
                contentAlignment = Alignment.Center,
            ) {
                if (selected) {
                    Text("✓", color = Color.White, fontWeight = FontWeight.Bold)
                }
            }
        }
        if (item.type == MediaType.VIDEO) {
            Text(
                text = formatDuration(item.durationMilliseconds) ?: strings.video,
                color = Color.White,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier
                    .padding(6.dp)
                    .background(Color.Black.copy(alpha = 0.55f), RoundedCornerShape(5.dp))
                    .padding(horizontal = 7.dp, vertical = 3.dp),
            )
        }
    }
}

@Composable
private fun MediaThumbnailPreview(
    item: MediaRecord?,
    mediaRepository: MediaRepository?,
    modifier: Modifier = Modifier,
    fallbackText: String,
    fallbackBackground: Color = LgCanvas,
) {
    val cacheKey = item?.let { record ->
        "${record.id}:${record.generation ?: record.modifiedAt.toEpochMilli()}:256"
    }
    var image by remember(cacheKey, mediaRepository) {
        mutableStateOf(cacheKey?.let(ThumbnailMemoryCache::get))
    }
    LaunchedEffect(cacheKey, mediaRepository) {
        if (image != null) return@LaunchedEffect
        val record = item ?: return@LaunchedEffect
        val repository = mediaRepository ?: return@LaunchedEffect
        val decoded = ThumbnailLoadGate.withPermit {
            withContext(Dispatchers.IO) {
                when (val result = repository.getThumbnail(record.id, 256, 256)) {
                    is MediaThumbnailResult.Found -> BitmapFactory
                        .decodeByteArray(result.jpeg, 0, result.jpeg.size)
                        ?.asImageBitmap()
                    else -> null
                }
            }
        }
        if (decoded != null && cacheKey != null) {
            ThumbnailMemoryCache.put(cacheKey, decoded)
        }
        image = decoded
    }

    if (image != null) {
        Image(
            bitmap = image!!,
            contentDescription = item?.fileName,
            contentScale = ContentScale.Crop,
            modifier = modifier,
        )
    } else {
        Box(
            modifier = modifier.background(fallbackBackground),
            contentAlignment = Alignment.Center,
        ) {}
    }
}

@Composable
private fun TopBar(
    title: String,
    subtitle: String,
    action: String,
    onAction: () -> Unit,
    eyebrow: String? = null,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 10.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column {
            if (eyebrow != null) {
                Text(
                    eyebrow,
                    color = LgBlue,
                    style = MaterialTheme.typography.labelMedium,
                    fontWeight = FontWeight.SemiBold,
                )
            }
            Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            Text(subtitle, color = LgMuted, style = MaterialTheme.typography.bodySmall)
        }
        if (action.isNotBlank()) {
            PressScale(targetScale = 0.94f) { pressModifier ->
                TextButton(onClick = onAction, modifier = pressModifier) { Text(action) }
            }
        }
    }
}

@Composable
private fun PermissionGate(permissionGranted: Boolean, onPermissionRequest: () -> Unit, strings: UiStrings) {
    StateCard(
        title = if (permissionGranted) {
            strings.photoVideoAccessReady
        } else {
            strings.allowReadPhotosVideos
        },
        detail = strings.readOnlyAccessDetail,
        tag = "permission_status",
    )
    if (!permissionGranted) {
        Spacer(Modifier.height(12.dp))
        Button(
            onClick = onPermissionRequest,
            shape = RoundedCornerShape(12.dp),
            colors = ButtonDefaults.buttonColors(containerColor = LgBlueStrong),
            modifier = Modifier.testTag("grant_media_permission"),
        ) {
            Text(strings.grantReadOnlyAccess)
        }
    }
}

@Composable
private fun SettingRow(title: String, detail: String, trailing: @Composable () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 10.dp)
            .border(1.dp, LgLine, RoundedCornerShape(19.dp)),
        colors = CardDefaults.cardColors(containerColor = LgCanvas),
        shape = RoundedCornerShape(19.dp),
    ) {
        Row(
            modifier = Modifier.padding(14.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                Text(title, fontWeight = FontWeight.SemiBold)
                Text(detail, color = LgMuted, style = MaterialTheme.typography.bodySmall)
            }
            trailing()
        }
    }
}

@Composable
private fun LinkGallerySwitch(checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    val thumbOffset by animateFloatAsState(
        targetValue = if (checked) 20f else 0f,
        animationSpec = tween(180, easing = LgEase),
        label = "switch-thumb",
    )
    val background = if (checked) LgSuccess else LgLine
    Box(
        modifier = Modifier
            .size(width = 50.dp, height = 30.dp)
            .clip(RoundedCornerShape(999.dp))
            .background(background)
            .clickable { onCheckedChange(!checked) }
            .padding(3.dp),
        contentAlignment = Alignment.CenterStart,
    ) {
        Box(
            modifier = Modifier
                .offset(x = thumbOffset.dp)
                .size(24.dp)
                .clip(CircleShape)
                .background(Color.White),
        )
    }
}

@Composable
private fun SelectionActionBar(
    selectedCount: Int,
    onCopy: () -> Unit,
    strings: UiStrings,
) {
    Surface(
        color = LgInk.copy(alpha = 0.94f),
        shape = RoundedCornerShape(16.dp),
        shadowElevation = 8.dp,
    ) {
        Row(
            modifier = Modifier.padding(start = 16.dp, end = 8.dp, top = 7.dp, bottom = 7.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(strings.selectedCount(selectedCount), color = Color.White)
            Button(
                onClick = onCopy,
                shape = RoundedCornerShape(10.dp),
                contentPadding = PaddingValues(horizontal = 14.dp, vertical = 8.dp),
            ) {
                Text(strings.copySelected)
            }
        }
    }
}

@Composable
private fun ToastOverlay(message: String?, modifier: Modifier = Modifier) {
    AnimatedVisibility(
        visible = message != null,
        enter = fadeIn(tween(180, easing = LgEase)) +
            slideInVertically(tween(180, easing = LgEase)) { 14 },
        exit = fadeOut(tween(180, easing = LgEase)) +
            slideOutVertically(tween(180, easing = LgEase)) { 14 },
        modifier = modifier.padding(bottom = 18.dp),
    ) {
        Text(
            text = message.orEmpty(),
            color = Color.White,
            modifier = Modifier
                .background(LgInk.copy(alpha = 0.86f), RoundedCornerShape(999.dp))
                .padding(horizontal = 16.dp, vertical = 11.dp),
        )
    }
}

@Composable
private fun PressScale(
    targetScale: Float,
    onClick: () -> Unit = {},
    content: @Composable (Modifier) -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) targetScale else 1f,
        animationSpec = tween(80, easing = LgEase),
        label = "press-scale",
    )
    content(
        Modifier
            .graphicsLayer(scaleX = scale, scaleY = scale)
            .clickable(
                interactionSource = interaction,
                indication = null,
                onClick = onClick,
            ),
    )
}

@Composable
private fun StateCard(
    title: String,
    detail: String,
    tag: String? = null,
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (tag == null) Modifier else Modifier.testTag(tag)),
        shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = LgCanvas),
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(title, fontWeight = FontWeight.SemiBold)
            Text(
                detail,
                color = LgMuted,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
    }
}

private enum class AppTab(val symbol: String, val testTag: String) {
    Photos("▦", "tab_gallery"),
    Albums("▣", "tab_albums"),
    Connection("◉", "tab_connection"),
}

private enum class GalleryMode {
    AllPhotos,
    Albums,
}

private fun AppTab.label(strings: UiStrings): String = when (this) {
    AppTab.Photos -> strings.gallery
    AppTab.Albums -> strings.albums
    AppTab.Connection -> strings.devices
}

private enum class MediaFilter {
    All,
    Photos,
    Videos,
}

private fun MediaFilter.label(strings: UiStrings): String = when (this) {
    MediaFilter.All -> strings.all
    MediaFilter.Photos -> strings.photos
    MediaFilter.Videos -> strings.videos
}

private data class AlbumUi(
    val name: String,
    val count: Int,
    val color: Color,
    val tag: String? = null,
    val cover: MediaRecord? = null,
    val albumId: String? = null,
    val relativePath: String? = null,
    val isSmart: Boolean = false,
    val members: List<MediaRecord>? = null,
)

private fun buildSmartAlbums(items: List<MediaRecord>, strings: UiStrings): List<AlbumUi> {
    val videos = items.filter { it.type == MediaType.VIDEO }
    val photos = items.filter { it.type == MediaType.IMAGE }
    val screenshots = items.filter {
            it.albumName?.contains("screenshot", ignoreCase = true) == true ||
                it.fileName.contains("screenshot", ignoreCase = true)
    }
    val recentCutoff = java.time.Instant.now().minusSeconds(30L * 24L * 60L * 60L)
    val recent = items.filter { it.takenAt >= recentCutoff }

    return listOf(
        AlbumUi(
            strings.photos,
            photos.size,
            Color(0xFFB7C9D6),
            strings.smartTag,
            isSmart = true,
            members = photos,
        ),
        AlbumUi(
            strings.videos,
            videos.size,
            Color(0xFFD9B8A7),
            strings.smartTag,
            isSmart = true,
            members = videos,
        ),
        AlbumUi(
            strings.screenshots,
            screenshots.size,
            Color(0xFF9FB6A0),
            strings.smartTag,
            isSmart = true,
            members = screenshots,
        ),
        AlbumUi(
            strings.recentlyAdded,
            recent.size,
            Color(0xFFC8B6D9),
            strings.smartTag,
            isSmart = true,
            members = recent,
        ),
    )
}

private fun buildDeviceAlbums(items: List<MediaRecord>, strings: UiStrings): List<AlbumUi> {
    val colors = listOf(LgBlueSoft, Color(0xFFF5F5F7), Color(0xFFFAFAFC))
    return items
        .groupBy {
            it.albumId
                ?: it.relativePath?.takeIf(String::isNotBlank)
                ?: "__unsorted"
        }
        .toList()
        .sortedByDescending { (_, albumItems) -> albumItems.maxOf { it.takenAt } }
        .take(8)
        .mapIndexed { index, (_, albumItems) ->
            val newestFirst = albumItems.sortedByDescending { it.takenAt }
            val first = newestFirst.first()
            AlbumUi(
                name = first.albumName?.takeIf(String::isNotBlank) ?: strings.unsorted,
                count = albumItems.size,
                color = colors[index % colors.size],
                tag = strings.deviceTag,
                cover = first,
                albumId = first.albumId,
                relativePath = first.relativePath,
                members = albumItems.takeIf { first.albumId == null },
            )
        }
}

private fun formatDuration(durationMilliseconds: Long?): String? {
    val totalSeconds = durationMilliseconds?.div(1_000) ?: return null
    val hours = totalSeconds / 3_600
    val minutes = (totalSeconds % 3_600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) {
        "%d:%02d:%02d".format(Locale.ROOT, hours, minutes, seconds)
    } else {
        "%d:%02d".format(Locale.ROOT, minutes, seconds)
    }
}

private enum class UiLanguage {
    Chinese,
    English,
}

private class UiStrings(val uiLanguage: UiLanguage) {
    fun t(english: String, chinese: String): String =
        if (uiLanguage == UiLanguage.English) english else chinese

    val photos get() = t("Photos", "照片")
    val gallery get() = t("Gallery", "图库")
    val allPhotos get() = t("All photos", "全部照片")
    val albums get() = t("Albums", "相册")
    val back get() = t("Back", "返回")
    val devices get() = t("Devices", "设备")
    val settings get() = t("Settings", "设置")
    val newAlbum get() = t("New album", "新建相册")
    val multiSelect get() = t("Multi-select", "多选")
    val all get() = t("All", "全部")
    val videos get() = t("Videos", "视频")
    val video get() = t("Video", "视频")
    val photo get() = t("Photo", "照片")
    val screenshots get() = t("Screenshots", "截图")
    val recentlyAdded get() = t("Recently Added", "最近添加")
    val smartAlbums get() = t("Smart Albums", "智能相册")
    val deviceAlbums get() = t("Device Albums", "设备相册")
    val myAlbums get() = t("My Albums", "我的相册")
    val seeAll get() = t("See all", "查看全部")
    val manage get() = t("Manage", "管理")
    val noCustomAlbums get() = t("No custom albums yet", "还没有自定义相册")
    val noAlbumsYet get() = t("No albums yet", "还没有相册")
    val noMediaFound get() = t("No media found", "没有找到媒体")
    val albumMayBeEmpty get() = t(
        "This album may be empty or its media was moved on the phone.",
        "这个相册可能为空，或其中的媒体已在手机上移动。",
    )
    val albumUnavailable get() = t(
        "This smart album will be available after indexing.",
        "此智能相册将在索引完成后可用。",
    )
    val galleryCursorInvalid get() = t("Gallery cursor is invalid.", "图库分页游标无效。")
    val keepOpenForWindows get() = t("Keep the app open while Windows connects.", "请保持应用打开，等待电脑连接。")
    val loadingMedia get() = t("Loading media...", "正在加载媒体…")
    val preparingFirstPage get() = t("Preparing the first read-only page.", "正在准备第一页只读媒体。")
    val unableToLoadMedia get() = t("Unable to load media", "无法加载媒体")
    val selectionModeEnabled get() = t("Selection mode enabled", "已进入多选")
    val wifiMediaService get() = t("Wi-Fi media service", "Wi-Fi 媒体服务")
    val languageLabel get() = t("Language", "语言")
    val chooseLanguage get() = t("Choose interface language", "选择界面语言")
    val serviceRunning get() = t("Service running", "服务运行中")
    val serviceStopped get() = t("Service stopped", "服务已停止")
    val keepServiceRunning get() = t("Keep service running", "保持服务运行")
    val allowPairedComputers get() = t("Allow paired computers to browse media", "允许已配对电脑浏览媒体")
    val mediaServiceStarted get() = t("Media service started", "媒体服务已启动")
    val mediaServiceStopped get() = t("Media service stopped", "媒体服务已停止")
    val pairedComputer get() = t("Paired computer", "已配对电脑")
    val noPairedDesktop get() = t("No paired desktop yet", "还没有已配对电脑")
    val pair get() = t("Pair", "配对")
    val savingToComputer get() = t("Saving to computer", "正在保存到电脑")
    val stopSharing get() = t("Stop sharing", "停止共享")
    val pocketSource get() = t("Pocket source", "Pocket 设备来源")
    val pocketIssueDetail get() = t("Shown after the Android device-source API is added", "等待 Android 设备来源接口完成后显示")
    val issue get() = t("Issue", "Issue")
    val mediaPermissionReady get() = t("Media permission ready", "媒体权限已就绪")
    val mediaPermissionNeeded get() = t("Media permission needed", "需要媒体权限")
    val readOnlyApiDetail get() = t(
        "The HTTP API is read-only. Windows can copy originals, but cannot delete or edit phone media.",
        "HTTP API 只读；电脑可以复制原文件，但不能删除或修改手机媒体。",
    )
    val pairAnotherComputer get() = t("Pair another computer", "配对另一台电脑")
    val albumsIndexedLater get() = t("Albums appear here after media is indexed.", "索引完成后相册会显示在这里。")
    val photoVideoAccessReady get() = t("Photo and video access is ready", "照片和视频访问已就绪")
    val allowReadPhotosVideos get() = t("Allow LinkGallery to read photos and videos", "允许 LinkGallery 读取照片和视频")
    val readOnlyAccessDetail get() = t("Access is read-only and used for local network browsing.", "访问权限为只读，仅用于局域网浏览。")
    val grantReadOnlyAccess get() = t("Grant read-only access", "授予只读访问权限")
    val waiting get() = t("Waiting", "等待中")
    val done get() = t("Done", "完成")
    val pairingCode get() = t("Pairing code", "配对码")
    val pairingCodeHelp get() = t(
        "Enter this code in LinkGallery for Windows. Codes are short-lived.",
        "在 Windows 端 LinkGallery 输入此代码；配对码会短时间后失效。",
    )
    val copyComplete get() = t("Copy complete", "复制完成")
    val copied get() = t("Copied", "已复制")
    val copySelected get() = t("Copy selected", "复制所选")
    val connectComputer get() = t("Connect a computer", "连接电脑")
    val connectComputerDetail get() = t(
        "Enable pairing, then enter this address code on Windows.",
        "开启配对后，在 Windows 端输入此地址码。",
    )
    val addressCode get() = t("Address code", "地址码")
    val addressCodeHelp get() = t(
        "This reversibly represents the complete IPv4 address.",
        "此代码可完整还原手机 IPv4 地址。",
    )
    val addressUnavailable get() = t(
        "No usable IPv4 address is available.",
        "当前没有可用的 IPv4 地址。",
    )
    val enablePairing get() = t("Enable pairing for 2 minutes", "开启两分钟配对")
    val waitingForWindows get() = t("Waiting for Windows to connect", "正在等待 Windows 连接")
    fun pairingReady(code: String): String {
        val formatted = if (code.length == 8) Ipv4AddressCode.format(code) else code.chunked(3).joinToString(" ")
        return t("Waiting for Windows · $formatted", "正在等待 Windows · $formatted")
    }
    val smartTag get() = t("SMART", "智能")
    val deviceTag get() = t("DEVICE", "设备")
    val unsorted get() = t("Unsorted", "未分类")

    fun itemCount(value: Int): String =
        t("${formatCount(value)} items", "${formatCount(value)} 项")

    fun selectedCount(value: Int): String = t("Selected $value", "已选 $value 张")

    fun transferProgress(completed: Int, total: Int, progress: Float): String = t(
        "$completed/$total items · ${(progress * 100).toInt()}%",
        "$completed/$total 项 · ${(progress * 100).toInt()}%",
    )

    fun gridColumns(columns: Int): String = t("$columns columns", "$columns 列")
}

private fun formatCount(value: Int): String =
    NumberFormat.getNumberInstance(Locale.getDefault()).format(value)

private sealed interface GalleryState {
    data object Loading : GalleryState
    data object Empty : GalleryState
    data object PermissionRequired : GalleryState
    data class Ready(val items: List<MediaRecord>) : GalleryState
    data class Error(val message: String) : GalleryState
}
