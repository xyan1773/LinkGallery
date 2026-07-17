package com.linkgallery.companion.media

import java.util.Locale

data class MediaSourceClassification(
    val sourceDevice: String? = null,
    val sourceApplication: String? = null,
    val isEditedExport: Boolean = false,
)

/**
 * Conservative Pocket 3/Mimo classifier.
 *
 * A DJI-looking filename by itself is intentionally insufficient: phones can contain
 * unrelated DJI downloads and renamed files. Pocket 3 is emitted only when a strong
 * model signal exists, or a DJI filename is corroborated by a DJI/Mimo directory,
 * album, package, or metadata signal. Likewise, an edit is emitted only from an
 * explicit export/editor marker.
 */
object Pocket3MediaClassifier {
    private val djiFileName = Regex("^DJI_[0-9]{6,}.*", RegexOption.IGNORE_CASE)
    private val pocketMarker = Regex("(^|[^a-z0-9])(?:osmo[ _-]?)?pocket[ _-]?3([^a-z0-9]|$)")
    private val exportMarker = Regex("(^|[/ _.-])(export(?:ed)?|editor|edited|render(?:ed)?)([/ _.-]|$)")

    fun classify(
        fileName: String,
        albumName: String?,
        relativePath: String?,
        mimeType: String? = null,
        ownerPackageName: String? = null,
        metadataMake: String? = null,
        metadataModel: String? = null,
        codec: String? = null,
    ): MediaSourceClassification {
        val name = normalize(fileName)
        val album = normalize(albumName)
        val path = normalize(relativePath)
        val owner = normalize(ownerPackageName)
        val make = normalize(metadataMake)
        val model = normalize(metadataModel)
        val encoding = normalize(codec)
        val mime = normalize(mimeType)

        val isMedia = mime.isEmpty() || mime.startsWith("image/") || mime.startsWith("video/")
        if (!isMedia) return MediaSourceClassification()

        val mimoEvidence = listOf(album, path, owner).any {
            it.contains("dji mimo") || it.contains("dji_mimo") || it.contains("dji.mimo")
        }
        val explicitPocket = listOf(album, path, model).any(pocketMarker::containsMatchIn)
        val djiMetadata = make == "dji" || make.contains("dji") ||
            model.contains("dji") || encoding.contains("dji")
        val djiLocation = album.contains("dji") || path.contains("dji")
        val corroboratedDjiName = djiFileName.containsMatchIn(name) &&
            (mimoEvidence || djiLocation || djiMetadata)
        val isPocket3 = explicitPocket || corroboratedDjiName

        val explicitExport = listOf(name, album, path).any(exportMarker::containsMatchIn)
        return MediaSourceClassification(
            sourceDevice = if (isPocket3) POCKET_3 else null,
            sourceApplication = if (mimoEvidence) DJI_MIMO else null,
            isEditedExport = mimoEvidence && explicitExport,
        )
    }

    private fun normalize(value: String?): String =
        value.orEmpty().trim().lowercase(Locale.ROOT).replace('\\', '/')

    const val POCKET_3 = "DJI Pocket 3"
    const val DJI_MIMO = "DJI Mimo"
}
