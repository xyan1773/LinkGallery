package com.linkgallery.companion.server

object ReadOnlyRoutePolicy {
    private val mediaReadRoutes = setOf(
        "/api/v1/device",
        "/api/v1/media",
        "/api/v1/media/{mediaId}/thumbnail",
        "/api/v1/media/{mediaId}/content",
    )

    fun permits(method: String, routeTemplate: String): Boolean =
        method.equals("GET", ignoreCase = true) && routeTemplate in mediaReadRoutes
}

