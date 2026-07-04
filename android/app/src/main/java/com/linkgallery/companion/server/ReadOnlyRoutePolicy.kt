package com.linkgallery.companion.server

object ReadOnlyRoutePolicy {
    private val mediaReadRoutes = setOf(
        "/api/v1/public/info",
        "/api/v1/device",
        "/api/v1/media",
        "/api/v1/media/sync/state",
        "/api/v1/media/changes",
        "/api/v1/media/manifest",
    )
    private val unauthenticatedPairingRoutes = setOf(
        "/api/v1/pair/start",
        "/api/v1/pair/confirm",
        "/api/v1/pair/cancel",
    )
    private const val PAIR_REVOKE_ROUTE = "/api/v1/pair/revoke"

    fun permits(method: String, routeTemplate: String): Boolean =
        if (method.equals("POST", ignoreCase = true)) {
            routeTemplate in unauthenticatedPairingRoutes || routeTemplate == PAIR_REVOKE_ROUTE
        } else {
            method.equals("GET", ignoreCase = true) &&
                (routeTemplate in mediaReadRoutes ||
                    THUMBNAIL_ROUTE.matches(routeTemplate) ||
                    CONTENT_ROUTE.matches(routeTemplate))
        }

    fun requiresAuthentication(method: String, routeTemplate: String): Boolean =
        permits(method, routeTemplate) &&
            routeTemplate != "/api/v1/public/info" &&
            routeTemplate !in unauthenticatedPairingRoutes

    private val THUMBNAIL_ROUTE =
        Regex("^/api/v1/media/[^/]+/thumbnail$")
    private val CONTENT_ROUTE =
        Regex("^/api/v1/media/[^/]+/content$")
}
