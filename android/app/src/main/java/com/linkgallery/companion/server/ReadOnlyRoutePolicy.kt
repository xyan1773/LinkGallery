package com.linkgallery.companion.server

object ReadOnlyRoutePolicy {
    private val mediaReadRoutes = setOf(
        "/api/v1/public/info",
        "/api/v1/device",
        "/api/v1/media",
    )

    fun permits(method: String, routeTemplate: String): Boolean =
        method.equals("GET", ignoreCase = true) &&
            (routeTemplate in mediaReadRoutes ||
                THUMBNAIL_ROUTE.matches(routeTemplate) ||
                CONTENT_ROUTE.matches(routeTemplate))

    private val THUMBNAIL_ROUTE =
        Regex("^/api/v1/media/[^/]+/thumbnail$")
    private val CONTENT_ROUTE =
        Regex("^/api/v1/media/[^/]+/content$")
}
