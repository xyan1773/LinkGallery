package com.linkgallery.companion.ui

import android.content.Context
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
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
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.linkgallery.companion.LinkGalleryServiceState
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaType
import java.text.NumberFormat
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val LgEase = CubicBezierEasing(0.2f, 0.8f, 0.2f, 1f)

@Composable
fun PermissionScreen(
    connectionGuide: ConnectionGuide,
    mediaRepository: MediaRepository? = null,
    serviceState: LinkGalleryServiceState = LinkGalleryServiceState(running = true),
    onServiceRunningChange: (Boolean) -> Unit = {},
    onOpenPairingWindow: () -> Long = { 0L },
    activePairingCodeProvider: () -> String? = { "428913" },
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
        activePairingCodeProvider = activePairingCodeProvider,
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
    onOpenPairingWindow: () -> Long = { 0L },
    activePairingCodeProvider: () -> String? = { "428913" },
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
    var selectedTab by remember { mutableStateOf(AppTab.Albums) }
    var selectedFilter by remember { mutableStateOf(MediaFilter.All) }
    var showPairing by remember { mutableStateOf(false) }
    var galleryState by remember { mutableStateOf<GalleryState>(GalleryState.Loading) }
    var toastMessage by remember { mutableStateOf<String?>(null) }

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
            NavigationBar(
                modifier = Modifier.testTag("bottom_navigation"),
                containerColor = LgCanvas,
                tonalElevation = 0.dp,
            ) {
                AppTab.entries.forEach { tab ->
                NavigationBarItem(
                    selected = selectedTab == tab,
                    onClick = { selectedTab = tab },
                    label = { Text(tab.label(strings)) },
                    icon = { Text(tab.symbol, fontWeight = FontWeight.SemiBold) },
                    modifier = Modifier.testTag(tab.testTag),
                )
                }
            }
        },
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .drawBehind {
                    drawRect(
                        brush = Brush.radialGradient(
                            colors = listOf(Color(0x140066CC), Color.Transparent),
                            center = androidx.compose.ui.geometry.Offset(size.width * 0.18f, size.height * 0.10f),
                            radius = size.width * 0.5f
                        )
                    )
                    drawRect(
                        brush = Brush.radialGradient(
                            colors = listOf(Color(0x0F34C759), Color.Transparent),
                            center = androidx.compose.ui.geometry.Offset(size.width * 0.88f, size.height * 0.86f),
                            radius = size.width * 0.4f
                        )
                    )
                },
        ) {
            Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
                AnimatedContent(
                    targetState = selectedTab,
                    transitionSpec = {
                        (fadeIn(tween(180, easing = LgEase)) +
                            slideInVertically(tween(180, easing = LgEase)) { 8 })
                            .togetherWith(
                                fadeOut(tween(1, easing = LgEase)) +
                                    slideOutVertically(tween(1, easing = LgEase)) { 0 },
                            )
                    },
                    label = "view-switch",
                ) { tab ->
                    when (tab) {
                        AppTab.Photos -> PhotosPage(
                            galleryState = galleryState,
                            selectedFilter = selectedFilter,
                            mediaRepository = mediaRepository,
                            onFilterChange = {
                                selectedFilter = it
                                showToast(strings.t("Showing ${it.label(strings).lowercase()}", "正在显示${it.label(strings)}"))
                            },
                            onPermissionRequest = onPermissionRequest,
                            onToast = ::showToast,
                            strings = strings,
                        )
                        AppTab.Albums -> AlbumsPage(
                            galleryState = galleryState,
                            mediaRepository = mediaRepository,
                            permissionGranted = permissionGranted,
                            onPermissionRequest = onPermissionRequest,
                            onManageDevices = { selectedTab = AppTab.Connection },
                            onToast = ::showToast,
                            strings = strings,
                        )
                        AppTab.Connection -> DevicesPage(
                            connectionGuide = connectionGuide,
                            serviceState = serviceState,
                            onServiceRunningChange = onServiceRunningChange,
                            permissionGranted = permissionGranted,
                            onPair = {
                                onOpenPairingWindow()
                                showPairing = true
                                showToast(strings.t("Pairing request created", "已创建配对请求"))
                            },
                            onToast = ::showToast,
                            strings = strings,
                            language = language,
                            onLanguageChange = ::setLanguage,
                        )
                    }
                }
            }
            ToastOverlay(
                message = toastMessage,
                modifier = Modifier.align(Alignment.BottomCenter),
            )
        }
    }

    if (showPairing) {
        PairingCodeDialog(
            code = serviceState.pairingCode ?: activePairingCodeProvider(),
            onDismiss = { showPairing = false },
            strings = strings,
        )
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
private fun AlbumsPage(
    galleryState: GalleryState,
    mediaRepository: MediaRepository?,
    permissionGranted: Boolean,
    onPermissionRequest: () -> Unit,
    onManageDevices: () -> Unit,
    onToast: (String) -> Unit,
    strings: UiStrings,
) {
    val mediaItems = (galleryState as? GalleryState.Ready)?.items.orEmpty()
    val smartAlbums = remember(mediaItems, strings.uiLanguage) { buildSmartAlbums(mediaItems, strings) }
    val deviceAlbums = remember(mediaItems, strings.uiLanguage) { buildDeviceAlbums(mediaItems, strings) }
    val customAlbums = remember(mediaItems) { emptyList<AlbumUi>() }
    val scope = rememberCoroutineScope()
    var activeAlbum by remember { mutableStateOf<AlbumUi?>(null) }
    var activeAlbumItems by remember { mutableStateOf<List<MediaRecord>>(emptyList()) }
    var albumLoading by remember { mutableStateOf(false) }

    if (activeAlbum != null) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(20.dp),
        ) {
            TopBar(
                title = activeAlbum!!.name,
                subtitle = strings.itemCount(activeAlbumItems.size),
                action = strings.back,
                onAction = {
                    activeAlbum = null
                    activeAlbumItems = emptyList()
                },
            )
            if (albumLoading) {
                StateCard(strings.loadingMedia, strings.preparingFirstPage)
            } else if (activeAlbumItems.isEmpty()) {
                StateCard(strings.noMediaFound, strings.albumMayBeEmpty)
            } else {
                MediaGrid(
                    items = activeAlbumItems,
                    selectedFilter = MediaFilter.All,
                    mediaRepository = mediaRepository,
                    onToast = onToast,
                    strings = strings,
                )
            }
        }
        return
    }

    fun openAlbum(album: AlbumUi) {
        if (album.albumId == null || mediaRepository == null) {
            onToast(strings.albumUnavailable)
            return
        }
        activeAlbum = album
        albumLoading = true
        scope.launch {
            activeAlbumItems = when (val result = withContext(Dispatchers.IO) {
                mediaRepository.getPage(MediaQuery(limit = 200, albumId = album.albumId))
            }) {
                is MediaPageResult.Success -> result.page.items
                else -> emptyList()
            }
            albumLoading = false
        }
    }
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
    ) {
        TopBar(
            title = strings.albums,
            subtitle = strings.itemCount(mediaItems.size),
            action = strings.newAlbum,
            onAction = { onToast(strings.newAlbum) },
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
            strings.seeAll,
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
        Spacer(Modifier.height(20.dp))
        AlbumSection(
            title = strings.myAlbums,
            action = null,
            albums = customAlbums,
            mediaRepository = mediaRepository,
            emptyMessage = strings.noCustomAlbums,
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
) {
    val mediaCount = (galleryState as? GalleryState.Ready)?.items?.size ?: 0
    Box(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(20.dp),
        ) {
            TopBar(
                title = strings.photos,
                subtitle = strings.itemCount(mediaCount),
                action = strings.multiSelect,
                onAction = { onToast(strings.selectionModeEnabled) },
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
                    onToast = onToast,
                    strings = strings,
                )
            }
        }
        FloatingCopyButton(
            onToast = onToast,
            strings = strings,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(horizontal = 20.dp, vertical = 24.dp),
        )
    }
}

@Composable
private fun DevicesPage(
    connectionGuide: ConnectionGuide,
    serviceState: LinkGalleryServiceState,
    onServiceRunningChange: (Boolean) -> Unit,
    permissionGranted: Boolean,
    onPair: () -> Unit,
    onToast: (String) -> Unit,
    strings: UiStrings,
    language: UiLanguage,
    onLanguageChange: (UiLanguage) -> Unit,
) {
    var showSettings by remember { mutableStateOf(false) }
    val serviceAddress = remember(serviceState.addresses, serviceState.port, connectionGuide.address) {
        val address = serviceState.addresses.firstOrNull()
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
            .padding(20.dp),
    ) {
        TopBar(
            title = strings.devices,
            subtitle = strings.wifiMediaService,
            action = strings.settings,
            onAction = { showSettings = !showSettings },
        )
        AnimatedVisibility(visible = showSettings) {
            SettingRow(
                title = strings.languageLabel,
                detail = strings.chooseLanguage,
                trailing = {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        TextButton(onClick = { onLanguageChange(UiLanguage.Chinese) }) {
                            Text(
                                text = "中文",
                                color = if (language == UiLanguage.Chinese) LgBlue else LgMuted,
                                fontWeight = FontWeight.SemiBold,
                            )
                        }
                        TextButton(onClick = { onLanguageChange(UiLanguage.English) }) {
                            Text(
                                text = "English",
                                color = if (language == UiLanguage.English) LgBlue else LgMuted,
                                fontWeight = FontWeight.SemiBold,
                            )
                        }
                    }
                },
            )
        }
        Card(
            colors = CardDefaults.cardColors(containerColor = LgBlue),
            shape = RoundedCornerShape(24.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Column(Modifier.padding(18.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        Modifier
                            .size(9.dp)
                            .clip(CircleShape)
                            .background(if (serviceState.running) LgSuccess else LgMuted),
                    )
                    Text(
                        text = if (serviceState.running) strings.serviceRunning else strings.serviceStopped,
                        color = Color.White,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
                Text(
                    text = connectionGuide.title,
                    color = Color.White,
                    style = MaterialTheme.typography.headlineSmall,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(top = 18.dp),
                )
                Text(
                    text = serviceAddress,
                    color = Color.White.copy(alpha = 0.88f),
                    modifier = Modifier
                        .padding(top = 8.dp)
                        .testTag("connection_address"),
                )
            }
        }
        Spacer(Modifier.height(16.dp))
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
            title = strings.pairedComputer,
            detail = serviceState.pairedDesktopNames
                .takeIf { it.isNotEmpty() }
                ?.joinToString()
                ?: strings.noPairedDesktop,
            trailing = {
                Text(strings.pair, color = LgBlue, fontWeight = FontWeight.SemiBold)
            },
        )
        SettingRow(
            title = strings.pocketSource,
            detail = strings.pocketIssueDetail,
            trailing = {
                Text(strings.issue, color = LgMuted, fontWeight = FontWeight.SemiBold)
            },
        )
        Spacer(Modifier.height(16.dp))
        StateCard(
            title = if (permissionGranted) strings.mediaPermissionReady else strings.mediaPermissionNeeded,
            detail = strings.readOnlyApiDetail,
            tag = "permission_status",
        )
        Spacer(Modifier.height(12.dp))
        Button(
            onClick = onPair,
            shape = RoundedCornerShape(999.dp),
            modifier = Modifier
                .fillMaxWidth()
                .testTag("show_pairing_code"),
            colors = ButtonDefaults.buttonColors(containerColor = LgBlueStrong),
        ) {
            Text(strings.pairAnotherComputer)
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
        modifier = Modifier.height(((albums.size + 1) / 2 * 190).dp),
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
    Column(modifier = Modifier.clickable { onClick(album) }) {
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
            )
            if (album.tag != null) {
                Text(
                    text = album.tag,
                    color = Color.White,
                    style = MaterialTheme.typography.labelSmall,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier
                        .padding(start = 12.dp, top = 12.dp)
                        .background(Color.Black.copy(alpha = 0.22f), RoundedCornerShape(999.dp))
                        .padding(horizontal = 8.dp, vertical = 3.dp),
                )
            }
        }
        Text(
            text = album.name,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(top = 8.dp),
        )
        Text(
            text = strings.itemCount(album.count),
            color = LgMuted,
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun MediaGrid(
    items: List<MediaRecord>,
    selectedFilter: MediaFilter,
    mediaRepository: MediaRepository?,
    onToast: (String) -> Unit,
    strings: UiStrings,
) {
    val filtered = when (selectedFilter) {
        MediaFilter.All -> items
        MediaFilter.Photos -> items.filter { it.type == MediaType.IMAGE }
        MediaFilter.Videos -> items.filter { it.type == MediaType.VIDEO }
    }
    val grouped = filtered.sortedByDescending { it.takenAt }.groupBy {
        it.takenAt.atZone(ZoneId.systemDefault()).toLocalDate()
    }
    LazyVerticalGrid(
        columns = GridCells.Fixed(3),
        modifier = Modifier
            .fillMaxSize()
            .testTag("gallery_grid"),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        grouped.forEach { (date, dateItems) ->
            item(span = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }) {
                Text(
                    text = date.format(DateTimeFormatter.ISO_LOCAL_DATE),
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(top = 8.dp, bottom = 4.dp),
                )
            }
            items(dateItems) { item ->
                MediaTile(item, mediaRepository, onToast, strings)
            }
        }
    }
}

@Composable
private fun MediaTile(
    item: MediaRecord,
    mediaRepository: MediaRepository?,
    onToast: (String) -> Unit,
    strings: UiStrings,
) {
    val shape = RoundedCornerShape(8.dp)
    PressScale(targetScale = 0.96f) { pressModifier ->
        Box(
            modifier = pressModifier
                .aspectRatio(1f)
                .clip(shape)
                .background(LgCanvas)
                .border(1.dp, LgLine, shape)
                .clickable { onToast(strings.t("Selected ${item.fileName}", "已选择 ${item.fileName}")) }
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
            Text(
                text = if (item.type == MediaType.VIDEO) strings.video else strings.photo,
                color = Color.White,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier
                    .padding(6.dp)
                    .background(Color.Black.copy(alpha = 0.28f), RoundedCornerShape(999.dp))
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
) {
    var image by remember(item?.id, mediaRepository) { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(item?.id, mediaRepository) {
        image = null
        val record = item ?: return@LaunchedEffect
        val repository = mediaRepository ?: return@LaunchedEffect
        val result = withContext(Dispatchers.IO) {
            repository.getThumbnail(record.id, 256, 256)
        }
        image = when (result) {
            is MediaThumbnailResult.Found -> BitmapFactory
                .decodeByteArray(result.jpeg, 0, result.jpeg.size)
                ?.asImageBitmap()
            else -> null
        }
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
            modifier = modifier.background(LgCanvas),
            contentAlignment = Alignment.Center,
        ) {}
    }
}

@Composable
private fun TopBar(title: String, subtitle: String, action: String, onAction: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 18.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column {
            Text("LinkGallery", style = MaterialTheme.typography.labelMedium, color = LgMuted)
            Text(title, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
            Text(subtitle, color = LgMuted, style = MaterialTheme.typography.bodySmall)
        }
        PressScale(targetScale = 0.94f) { pressModifier ->
            TextButton(onClick = onAction, modifier = pressModifier) { Text(action) }
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
            shape = RoundedCornerShape(999.dp),
            colors = ButtonDefaults.buttonColors(containerColor = LgBlueStrong),
            modifier = Modifier.testTag("grant_media_permission"),
        ) {
            Text(strings.grantReadOnlyAccess)
        }
    }
}

@Composable
private fun PairingCodeDialog(code: String?, onDismiss: () -> Unit, strings: UiStrings) {
    val displayCode = code?.chunked(3)?.joinToString(" ") ?: strings.waiting
    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(strings.done) }
        },
        title = { Text(strings.pairingCode) },
        text = {
            Column {
                Text(
                    text = displayCode,
                    style = MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.testTag("pairing_code"),
                )
                Spacer(Modifier.height(8.dp))
                Text(strings.pairingCodeHelp)
            }
        },
    )
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
private fun FloatingCopyButton(onToast: (String) -> Unit, strings: UiStrings, modifier: Modifier = Modifier) {
    var copying by remember { mutableStateOf(false) }
    var progress by remember { mutableStateOf(0f) }
    LaunchedEffect(copying) {
        if (copying) {
            progress = 0f
            while (progress < 100f) {
                delay(90)
                progress += 4f
            }
            onToast(strings.copyComplete)
            delay(900)
            copying = false
            progress = 0f
        }
    }

    PressScale(targetScale = 0.97f) { pressModifier ->
        Box(
            modifier = modifier
                .then(pressModifier)
                .fillMaxWidth()
                .height(58.dp)
                .clip(RoundedCornerShape(999.dp))
                .background(if (progress >= 100f) LgSuccess else LgBlue)
                .clickable(enabled = !copying) { copying = true },
            contentAlignment = Alignment.Center,
        ) {
            Box(
                modifier = Modifier
                    .align(Alignment.CenterStart)
                    .fillMaxHeight()
                    .fillMaxWidth((progress / 100f).coerceIn(0f, 1f))
                    .background(Color.White.copy(alpha = 0.16f)),
            )
            Text(
                text = when {
                    progress >= 100f -> strings.copied
                    copying -> strings.t("Copying... ${progress.toInt()}%", "正在复制… ${progress.toInt()}%")
                    else -> strings.copySelected
                },
                color = Color.White,
                fontWeight = FontWeight.Bold,
            )
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
                onClick = {},
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
        shape = RoundedCornerShape(8.dp),
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
    Photos("P", "tab_gallery"),
    Albums("A", "tab_albums"),
    Connection("D", "tab_connection"),
}

private fun AppTab.label(strings: UiStrings): String = when (this) {
    AppTab.Photos -> strings.photos
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
)

private fun buildSmartAlbums(items: List<MediaRecord>, strings: UiStrings): List<AlbumUi> {
    val videos = items.filter { it.type == MediaType.VIDEO }
    val photos = items.filter { it.type == MediaType.IMAGE }
    val screenshots = items.filter {
            it.albumName?.contains("screenshot", ignoreCase = true) == true ||
                it.fileName.contains("screenshot", ignoreCase = true)
    }

    return listOfNotNull(
        videos.takeIf { it.isNotEmpty() }?.let {
            AlbumUi(strings.videos, it.size, Color(0xFF5AC8FA), strings.smartTag, it.first())
        },
        photos.takeIf { it.isNotEmpty() }?.let {
            AlbumUi(strings.photos, it.size, Color(0xFFAF52DE), strings.smartTag, it.first())
        },
        screenshots.takeIf { it.isNotEmpty() }?.let {
            AlbumUi(strings.screenshots, it.size, Color(0xFFFFB340), strings.smartTag, it.first())
        },
    )
}

private fun buildDeviceAlbums(items: List<MediaRecord>, strings: UiStrings): List<AlbumUi> {
    val colors = listOf(LgSuccess, LgBlue, Color(0xFF8E8E93), Color(0xFF5856D6))
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
            )
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
    val smartTag get() = t("SMART", "智能")
    val deviceTag get() = t("DEVICE", "设备")
    val unsorted get() = t("Unsorted", "未分类")

    fun itemCount(value: Int): String =
        t("${formatCount(value)} items", "${formatCount(value)} 项")
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
