package com.linkgallery.companion.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

internal val LgBlue = Color(0xFF0066CC)
internal val LgBlueStrong = Color(0xFF0071E3)
internal val LgBlueSoft = Color(0xFFEAF3FF)
internal val LgInk = Color(0xFF1D1D1F)
internal val LgMuted = Color(0xFF6E6E73)
internal val LgCanvas = Color(0xFFFFFFFF)
internal val LgParchment = Color(0xFFF5F5F7)
internal val LgLine = Color(0xFFE0E0E0)
internal val LgSuccess = Color(0xFF34C759)
internal val LgDanger = Color(0xFFFF3B30)

private val LinkGalleryLightColors = lightColorScheme(
    primary = LgBlue,
    onPrimary = Color.White,
    primaryContainer = LgBlueSoft,
    onPrimaryContainer = LgInk,
    secondary = Color(0xFF5AC8FA),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFE7F7FE),
    onSecondaryContainer = LgInk,
    tertiary = LgSuccess,
    onTertiary = Color.White,
    background = LgParchment,
    onBackground = LgInk,
    surface = LgCanvas,
    onSurface = LgInk,
    surfaceVariant = Color(0xFFFAFAFC),
    onSurfaceVariant = LgMuted,
    outline = LgLine,
    error = LgDanger,
)

@Composable
fun LinkGalleryTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = LinkGalleryLightColors,
        content = content,
    )
}
