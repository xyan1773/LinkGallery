package com.linkgallery.companion.ui

import android.view.View
import com.journeyapps.barcodescanner.CaptureActivity
import com.journeyapps.barcodescanner.DecoratedBarcodeView
import com.linkgallery.companion.R

class LinkGalleryQrCaptureActivity : CaptureActivity() {
    override fun initializeContent(): DecoratedBarcodeView {
        setContentView(R.layout.activity_qr_capture)
        findViewById<View>(R.id.qr_close_button).setOnClickListener { finish() }
        return findViewById(R.id.zxing_barcode_scanner)
    }
}
