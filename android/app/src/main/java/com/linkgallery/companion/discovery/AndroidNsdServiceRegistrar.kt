package com.linkgallery.companion.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import com.linkgallery.companion.server.PublicDeviceInfoProvider

class AndroidNsdServiceRegistrar(
    context: Context,
    private val publicDeviceInfoProvider: PublicDeviceInfoProvider,
) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var listener: NsdManager.RegistrationListener? = null

    fun register(port: Int) {
        unregister()
        val announcement = LinkGalleryNsdAnnouncementFactory.create(publicDeviceInfoProvider.get(), port)
        val serviceInfo = NsdServiceInfo().apply {
            serviceName = announcement.serviceName
            serviceType = announcement.serviceType
            this.port = announcement.port
            announcement.attributes.forEach { (name, value) ->
                setAttribute(name, value)
            }
        }
        val registrationListener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                Log.i("LinkGalleryNsd", "Registered ${serviceInfo.serviceName}")
            }

            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.w("LinkGalleryNsd", "Registration failed: $errorCode")
                listener = null
            }

            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) {
                Log.i("LinkGalleryNsd", "Unregistered ${serviceInfo.serviceName}")
            }

            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.w("LinkGalleryNsd", "Unregistration failed: $errorCode")
            }
        }
        listener = registrationListener
        nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, registrationListener)
    }

    fun unregister() {
        val registrationListener = listener ?: return
        listener = null
        runCatching {
            nsdManager.unregisterService(registrationListener)
        }.onFailure {
            Log.w("LinkGalleryNsd", "Unable to unregister NSD service", it)
        }
    }
}
