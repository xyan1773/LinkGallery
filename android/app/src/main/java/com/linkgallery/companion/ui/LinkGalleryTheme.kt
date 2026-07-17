package com.linkgallery.companion.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

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
    secondary = LgBlue,
    onSecondary = Color.White,
    secondaryContainer = LgBlueSoft,
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

private val LinkGalleryTypography = Typography(
    displayLarge = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 40.sp,
        lineHeight = 44.sp,
        fontWeight = FontWeight.SemiBold,
    ),
    headlineMedium = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 28.sp,
        lineHeight = 32.sp,
        fontWeight = FontWeight.SemiBold,
    ),
    titleLarge = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 21.sp,
        lineHeight = 26.sp,
        fontWeight = FontWeight.SemiBold,
    ),
    bodyLarge = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 17.sp,
        lineHeight = 26.sp,
        fontWeight = FontWeight.Normal,
    ),
    bodyMedium = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 14.sp,
        lineHeight = 21.sp,
        fontWeight = FontWeight.Normal,
    ),
    bodySmall = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 14.sp,
        lineHeight = 20.sp,
        fontWeight = FontWeight.Normal,
    ),
    labelLarge = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 14.sp,
        lineHeight = 18.sp,
        fontWeight = FontWeight.SemiBold,
    ),
    labelSmall = TextStyle(
        fontFamily = FontFamily.SansSerif,
        fontSize = 12.sp,
        lineHeight = 16.sp,
        fontWeight = FontWeight.Normal,
    ),
)

@Composable
fun LinkGalleryTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = LinkGalleryLightColors,
        typography = LinkGalleryTypography,
        content = content,
    )
}
