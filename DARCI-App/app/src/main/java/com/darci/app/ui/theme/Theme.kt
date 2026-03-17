package com.darci.app.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val DarkColors = darkColorScheme(
    primary       = Color(0xFF7E57C2),
    onPrimary     = Color.White,
    secondary     = Color(0xFF26A69A),
    background    = Color(0xFF121212),
    surface       = Color(0xFF1E1E1E),
    surfaceVariant = Color(0xFF2C2C2C)
)

@Composable
fun DarciAppTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = DarkColors,
        content     = content
    )
}
