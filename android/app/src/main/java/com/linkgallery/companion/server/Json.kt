package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaPage
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaType

internal object Json {
    fun device(value: DeviceInfo): String = objectOf(
        "id" to string(value.id),
        "name" to string(value.name),
        "platform" to string("android"),
        "model" to nullableString(value.model),
        "battery" to value.battery?.toString().orNull(),
        "mediaCount" to value.mediaCount.toString(),
    )

    fun mediaPage(value: MediaPage): String = objectOf(
        "items" to value.items.joinToString(separator = ",", prefix = "[", postfix = "]") {
            mediaItem(it)
        },
        "nextCursor" to nullableString(value.nextCursor),
        "hasMore" to value.hasMore.toString(),
        "total" to value.total.toString(),
    )

    fun problem(code: String, message: String): String = objectOf(
        "code" to string(code),
        "message" to string(message),
    )

    fun pairingChallenge(value: PairingChallenge): String = objectOf(
        "sessionId" to string(value.sessionId),
        "confirmationCode" to string(value.confirmationCode),
        "expiresAt" to string(value.expiresAt),
    )

    fun pairingResult(value: PairingResult): String = objectOf(
        "deviceId" to string(value.deviceId),
        "devicePublicKey" to string(value.devicePublicKey),
        "accessToken" to string(value.accessToken),
        "expiresAt" to string(value.expiresAt),
    )

    private fun mediaItem(value: MediaRecord): String = objectOf(
        "id" to string(value.id),
        "fileName" to string(value.fileName),
        "type" to string(
            when (value.type) {
                MediaType.IMAGE -> "image"
                MediaType.VIDEO -> "video"
            },
        ),
        "fileSize" to value.fileSize.toString(),
        "width" to value.width?.toString().orNull(),
        "height" to value.height?.toString().orNull(),
        "durationMilliseconds" to value.durationMilliseconds?.toString().orNull(),
        "takenAt" to string(value.takenAt.toString()),
        "modifiedAt" to string(value.modifiedAt.toString()),
        "albumName" to nullableString(value.albumName),
        "relativePath" to nullableString(value.relativePath),
        "thumbnailUrl" to nullableString(value.thumbnailUrl),
        "sourceDevice" to nullableString(value.sourceDevice),
        "sourceApplication" to nullableString(value.sourceApplication),
        "isEditedExport" to value.isEditedExport.toString(),
    )

    private fun objectOf(vararg fields: Pair<String, String>): String =
        fields.joinToString(separator = ",", prefix = "{", postfix = "}") { (name, value) ->
            "${string(name)}:$value"
        }

    private fun nullableString(value: String?): String = value?.let(::string) ?: "null"

    private fun Any?.orNull(): String = this?.toString() ?: "null"

    private fun string(value: String): String = buildString {
        append('"')
        value.forEach { character ->
            when (character) {
                '"' -> append("\\\"")
                '\\' -> append("\\\\")
                '\b' -> append("\\b")
                '\u000C' -> append("\\f")
                '\n' -> append("\\n")
                '\r' -> append("\\r")
                '\t' -> append("\\t")
                else -> if (character.code < 0x20) {
                    append("\\u")
                    append(character.code.toString(16).padStart(4, '0'))
                } else {
                    append(character)
                }
            }
        }
        append('"')
    }
}
