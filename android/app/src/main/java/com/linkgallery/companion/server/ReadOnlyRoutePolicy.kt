package com.linkgallery.companion.server

object ReadOnlyRoutePolicy {
    private val mediaReadRoutes = setOf(
        "/api/v1/public/info",
        "/api/v1/device",
        "/api/v1/media",
    )
    private val unauthenticatedPairingRoutes = setOf(
        "/api/v1/pair/start",
        "/api/v1/pair/confirm",
        "/api/v1/pair/cancel",
    )

    fun permits(method: String, routeTemplate: String): Boolean =
        if (method.equals("POST", ignoreCase = true)) {
            routeTemplate in unauthenticatedPairingRoutes
        } else {
            method.equals("GET", ignoreCase = true) &&
                (routeTemplate in mediaReadRoutes ||
                    THUMBNAIL_ROUTE.matches(routeTemplate) ||
                    CONTENT_ROUTE.matches(routeTemplate))
        }

    private val THUMBNAIL_ROUTE =
        Regex("^/api/v1/media/[^/]+/thumbnail$")
    private val CONTENT_ROUTE =
        Regex("^/api/v1/media/[^/]+/content$")
}
