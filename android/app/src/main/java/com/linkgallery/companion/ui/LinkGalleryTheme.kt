package com.linkgallery.companion.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val LinkGalleryLightColors = lightColorScheme(
    primary = Color(0xFF0F7B6C),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDDF3EE),
    onPrimaryContainer = Color(0xFF052E2A),
    secondary = Color(0xFF415A77),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFDDE6F2),
    onSecondaryContainer = Color(0xFF102033),
    tertiary = Color(0xFF8A5A12),
    onTertiary = Color.White,
    background = Color(0xFFF7F8FA),
    onBackground = Color(0xFF17202A),
    surface = Color.White,
    onSurface = Color(0xFF17202A),
    surfaceVariant = Color(0xFFE8EDF2),
    onSurfaceVariant = Color(0xFF637083),
    error = Color(0xFFB42318),
)

@Composable
fun LinkGalleryTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = LinkGalleryLightColors,
        content = content,
    )
}
